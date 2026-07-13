// Geporteerd van Piltover-Archive/RiftboundDeckCodes (Apache License 2.0),
// zelf geadapteerd van Riot Games' LoRDeckCodes.
// Bron: https://github.com/Piltover-Archive/RiftboundDeckCodes

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Ongeldig deck-code- of kaartcode-invoer bij <see cref="DeckCode"/>.
/// Eén eigen exceptiesoort zodat een endpoint straks élke encode/decode-fout
/// als nette 400 kan mappen zonder framework-excepties te vangen.</summary>
public sealed class DeckCodeException(string message) : Exception(message);

/// <summary>Eén regel in een decklijst: kaartcode ("OGN-126", "SFD-R02",
/// "OGN-007a") plus het aantal exemplaren.</summary>
public readonly record struct DeckListEntry(string CardCode, int Count);

/// <summary>Een volledig deck zoals het deck-code-formaat het kent: main deck
/// (aantallen 1–12, runes zitten hier gewoon tussen als R-nummers), sideboard
/// (aantallen 1–3) en optioneel een chosen champion-kaartcode (versie 3+).
/// Spelregels (decklimieten, sideboard-grootte) valideert dit bewust niet —
/// net als de bron is dit puur het transportformaat.</summary>
public sealed record DeckList(
    IReadOnlyList<DeckListEntry> MainDeck,
    IReadOnlyList<DeckListEntry> Sideboard,
    string? ChosenChampion = null);

/// <summary>Encoderen/decoderen van Riftbound-deck-codes (base32 over een
/// varint-bytestroom, format 1, versies 1–4). Pure functies zonder I/O; alle
/// foutpaden gooien <see cref="DeckCodeException"/> met een uitlegbare
/// boodschap in plaats van ergens diep te crashen.</summary>
public static partial class DeckCode
{
    private const int Format = 1;

    /// <summary>Hoogste versie die deze implementatie kan lezen. De encoder
    /// schrijft versie 3, of 4 zodra er R-genummerde runekaarten in zitten —
    /// exact het gedrag van de referentie-implementatie, zodat codes van hier
    /// ook in oudere decoders zonder rune-support blijven werken.</summary>
    private const int MaxKnownVersion = 4;

    private const byte RuneFlag = 0x01;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Set-code naar numeriek id. Volgorde is heilig: nieuwe sets
    /// komen altijd achteraan (bestaande codes blijven dan stabiel).</summary>
    private static readonly Dictionary<string, byte> SetMap = new()
    {
        ["OGN"] = 0,
        ["OGS"] = 1,
        ["ARC"] = 2,
        ["SFD"] = 3,
        ["UNL"] = 4,
        ["VEN"] = 5,
        ["RAD"] = 6,
    };

    /// <summary>Variant-suffix naar numeriek id. "s" en "*" zijn allebei
    /// geldige notaties voor gesigneerde kaarten en delen id 2.</summary>
    private static readonly Dictionary<string, byte> VariantMap = new()
    {
        [""] = 0,
        ["a"] = 1,
        ["s"] = 2,
        ["*"] = 2,
        ["b"] = 3,
    };

    /// <summary>Kaartnummer-deel na "SET-": optioneel een R-prefix (rune),
    /// cijfers, en hooguit één variantletter of ster.</summary>
    [GeneratedRegex(@"^(R?\d+)([a-z*]?)$")]
    private static partial Regex CardNumberRegex();

    /// <summary>Encodeert een deck naar een deelbare deck-code.</summary>
    /// <exception cref="DeckCodeException">Bij een ongeldige kaartcode,
    /// onbekende set/variant of een aantal buiten het bereik van de sectie
    /// (main deck 1–12, sideboard 1–3).</exception>
    public static string Encode(DeckList deck)
    {
        // Afwijking van de bron: de TS-referentie laat aantallen buiten het
        // sectiebereik stilletjes uit de code weg (encode → decode verliest
        // dan kaarten). Dat is een dataverlies-val; wij weigeren expliciet.
        var version = NeedsRuneSupport(deck) ? 4 : 3;

        var bytes = new List<byte> { (byte)((Format << 4) | version) };
        EncodeSection(bytes, deck.MainDeck, maxCount: 12, version, "main deck");
        EncodeSection(bytes, deck.Sideboard, maxCount: 3, version, "sideboard");
        EncodeChampion(bytes, deck.ChosenChampion, version);
        return Base32Encode(bytes);
    }

    /// <summary>Decodeert een deck-code naar main deck, sideboard en chosen
    /// champion. Kleine letters in de code zijn toegestaan (net als de bron).</summary>
    /// <param name="signedSuffix">Suffix voor gesigneerde kaarten in het
    /// resultaat: 's' (standaard) of '*' — twee gangbare notaties voor
    /// dezelfde variant.</param>
    /// <exception cref="DeckCodeException">Bij een lege of corrupte code,
    /// onbekend format/versie of onbekende set-/variant-id's.</exception>
    public static DeckList Decode(string? code, char signedSuffix = 's')
    {
        if (signedSuffix is not ('s' or '*'))
            throw new ArgumentOutOfRangeException(nameof(signedSuffix), signedSuffix, "alleen 's' of '*' is een geldige signed-suffix");
        if (string.IsNullOrWhiteSpace(code))
            throw new DeckCodeException("Lege deck-code.");

        var reader = new ByteReader(Base32Decode(code.Trim()));

        var formatVersion = reader.ReadByte();
        var format = (formatVersion >> 4) & 0x0f;
        var version = formatVersion & 0x0f;
        if (format != Format)
            throw new DeckCodeException($"Niet-ondersteund format {format}; verwacht format {Format}.");
        if (version > MaxKnownVersion)
            throw new DeckCodeException($"Niet-ondersteunde versie {version}; deze implementatie kent tot en met versie {MaxKnownVersion}.");

        var mainDeck = DecodeSection(reader, maxCount: 12, signedSuffix, version);
        // Versie 1 had nog geen sideboard-sectie; versie 3+ voegde de
        // champion-byte toe. Oudere codes moeten leesbaar blijven.
        List<DeckListEntry> sideboard = version >= 2
            ? DecodeSection(reader, maxCount: 3, signedSuffix, version)
            : [];
        var champion = version >= 3 ? DecodeChampion(reader, signedSuffix, version) : null;

        return new DeckList(mainDeck, sideboard, champion);
    }

    /// <summary>Runekaarten (R-nummers) vereisen het flag-byte-formaat van
    /// versie 4; zonder runes encoderen we als versie 3 voor compatibiliteit.</summary>
    private static bool NeedsRuneSupport(DeckList deck)
    {
        var codes = deck.MainDeck.Select(c => c.CardCode)
            .Concat(deck.Sideboard.Select(c => c.CardCode));
        if (deck.ChosenChampion is { } champion) codes = codes.Append(champion);
        return codes.Any(code => ParseCardCode(code).Number.StartsWith('R'));
    }

    private static void EncodeSection(List<byte> bytes, IReadOnlyList<DeckListEntry> cards, int maxCount, int version, string sectionName)
    {
        foreach (var card in cards)
            if (card.Count < 1 || card.Count > maxCount)
                throw new DeckCodeException($"Ongeldig aantal {card.Count} voor {card.CardCode}: in het {sectionName} is 1 t/m {maxCount} toegestaan.");

        // Het formaat schrijft per aantal (aflopend van maxCount naar 1) de
        // set/variant-groepen; lege aantallen krijgen expliciet groepstal 0.
        for (var count = maxCount; count >= 1; count--)
        {
            var groups = GroupBySetAndVariant(cards.Where(c => c.Count == count));
            WriteVarint(bytes, groups.Count);

            foreach (var group in groups)
            {
                WriteVarint(bytes, group.CardNumbers.Count);
                bytes.Add(group.Set);
                bytes.Add(group.Variant);
                foreach (var number in group.CardNumbers)
                    WriteCardNumber(bytes, number, version);
            }
        }
    }

    private static void EncodeChampion(List<byte> bytes, string? champion, int version)
    {
        if (champion is null)
        {
            bytes.Add(0x00); // geen champion
            return;
        }

        var (set, number, variant) = ParseCardCode(champion);
        bytes.Add(0x01); // champion aanwezig
        bytes.Add(SetId(set));
        bytes.Add(VariantId(variant));
        WriteCardNumber(bytes, number, version);
    }

    /// <summary>Vanaf versie 4 gaat er een flag-byte vóór elk kaartnummer:
    /// 0x00 normaal, 0x01 rune (R-prefix). Oudere versies schrijven kaal.</summary>
    private static void WriteCardNumber(List<byte> bytes, string number, int version)
    {
        var isRune = number.StartsWith('R');
        if (version >= 4)
            bytes.Add(isRune ? RuneFlag : (byte)0x00);
        var digits = isRune ? number[1..] : number;
        WriteVarint(bytes, int.Parse(digits, CultureInfo.InvariantCulture));
    }

    private static List<DeckListEntry> DecodeSection(ByteReader reader, int maxCount, char signedSuffix, int version)
    {
        var deck = new List<DeckListEntry>();

        for (var count = maxCount; count >= 1; count--)
        {
            var numGroups = reader.ReadVarint();
            for (var i = 0; i < numGroups; i++)
            {
                var numCards = reader.ReadVarint();
                var setCode = SetCode(reader.ReadByte());
                var variantCode = VariantCode(reader.ReadByte(), signedSuffix);

                for (var j = 0; j < numCards; j++)
                {
                    var number = ReadCardNumber(reader, version);
                    deck.Add(new DeckListEntry($"{setCode}-{number}{variantCode}", count));
                }
            }
        }

        return deck;
    }

    private static string? DecodeChampion(ByteReader reader, char signedSuffix, int version)
    {
        if (reader.ReadByte() != 0x01) return null;

        var setCode = SetCode(reader.ReadByte());
        var variantCode = VariantCode(reader.ReadByte(), signedSuffix);
        var number = ReadCardNumber(reader, version);
        return $"{setCode}-{number}{variantCode}";
    }

    /// <summary>Leest een kaartnummer terug in tekstvorm: runenummers padden
    /// naar twee cijfers ("R02"), normale nummers naar drie ("007") — exact
    /// zoals de referentie ze weer opschrijft.</summary>
    private static string ReadCardNumber(ByteReader reader, int version)
    {
        var isRune = version >= 4 && reader.ReadByte() == RuneFlag;
        var number = reader.ReadVarint().ToString(CultureInfo.InvariantCulture);
        return isRune ? $"R{number.PadLeft(2, '0')}" : number.PadLeft(3, '0');
    }

    private static (string Set, string Number, string Variant) ParseCardCode(string cardCode)
    {
        var parts = cardCode.Split('-');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            throw new DeckCodeException($"Ongeldige kaartcode '{cardCode}': verwacht formaat is SET-NUMMERvariant, zoals OGN-007a.");

        var match = CardNumberRegex().Match(parts[1]);
        if (!match.Success)
            throw new DeckCodeException($"Ongeldige kaartcode '{cardCode}': verwacht formaat is SET-NUMMERvariant, zoals OGN-007a.");

        return (parts[0], match.Groups[1].Value, match.Groups[2].Value);
    }

    private static byte SetId(string set) =>
        SetMap.TryGetValue(set, out var id)
            ? id
            : throw new DeckCodeException($"Onbekende set '{set}'. Geldige sets: {string.Join(", ", SetMap.Keys)}.");

    private static byte VariantId(string variant) =>
        VariantMap.TryGetValue(variant, out var id)
            ? id
            : throw new DeckCodeException($"Onbekende variant '{variant}'. Geldige varianten: {string.Join(", ", VariantMap.Keys.Where(k => k.Length > 0))} of geen suffix.");

    private static string SetCode(byte id) =>
        SetMap.FirstOrDefault(kv => kv.Value == id).Key
            ?? throw new DeckCodeException($"Onbekende set-id {id} in de deck-code; mogelijk een nieuwere set dan deze implementatie kent.");

    /// <summary>Variant-id terug naar suffix. Id 2 (gesigneerd) volgt de
    /// gekozen <paramref name="signedSuffix"/>-notatie. Afwijking van de bron:
    /// de TS-referentie laat een ónbekende variant-id stilletjes wegvallen —
    /// dan decodeert een code naar de verkeerde kaart; wij weigeren.</summary>
    private static string VariantCode(byte id, char signedSuffix)
    {
        if (id == 2) return signedSuffix.ToString();
        return VariantMap.FirstOrDefault(kv => kv.Value == id).Key
            ?? throw new DeckCodeException($"Onbekende variant-id {id} in de deck-code; mogelijk een nieuwere variant dan deze implementatie kent.");
    }

    private sealed record SetVariantGroup(byte Set, byte Variant, List<string> CardNumbers);

    /// <summary>Groepeert kaartnummers per (set, variant), gesorteerd op set-id
    /// en dan variant-id; binnen een groep alfanumeriek op kaartnummer. Die
    /// canonieke volgorde maakt de encoding deterministisch: zelfde deck =
    /// zelfde code, ongeacht de invoervolgorde.</summary>
    private static List<SetVariantGroup> GroupBySetAndVariant(IEnumerable<DeckListEntry> cards)
    {
        var groups = new Dictionary<(byte Set, byte Variant), SetVariantGroup>();

        foreach (var card in cards)
        {
            var (set, number, variant) = ParseCardCode(card.CardCode);
            var key = (SetId(set), VariantId(variant));
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = new SetVariantGroup(key.Item1, key.Item2, []);
            group.CardNumbers.Add(number);
        }

        var sorted = groups.Values
            .OrderBy(g => g.Set)
            .ThenBy(g => g.Variant)
            .ToList();
        foreach (var group in sorted)
            group.CardNumbers.Sort(CompareCardNumbers);
        return sorted;
    }

    /// <summary>Nummervolgorde binnen een groep. De referentie gebruikt
    /// localeCompare met numeric-collatie; voor de vormen die de regex toelaat
    /// (R?\d+) komt dat exact neer op: normale nummers vóór runenummers, dan
    /// numeriek, dan ordinaal als tie-break (voorloopnullen).</summary>
    private static int CompareCardNumbers(string a, string b)
    {
        var aRune = a.StartsWith('R');
        var bRune = b.StartsWith('R');
        if (aRune != bRune) return aRune ? 1 : -1;

        var byValue = int.Parse(aRune ? a[1..] : a, CultureInfo.InvariantCulture)
            .CompareTo(int.Parse(bRune ? b[1..] : b, CultureInfo.InvariantCulture));
        return byValue != 0 ? byValue : string.CompareOrdinal(a, b);
    }

    /// <summary>Varints zoals in LoRDeckCodes: 7 databits per byte, hoogste
    /// bit is het vervolg-vlaggetje.</summary>
    private static void WriteVarint(List<byte> bytes, int value)
    {
        if (value == 0)
        {
            bytes.Add(0);
            return;
        }

        var remaining = (uint)value;
        while (remaining != 0)
        {
            var chunk = (byte)(remaining & 0x7f);
            remaining >>= 7;
            if (remaining != 0) chunk |= 0x80;
            bytes.Add(chunk);
        }
    }

    /// <summary>RFC 4648-base32 zonder padding: 5 bits per teken, restbits
    /// met nullen aangevuld.</summary>
    private static string Base32Encode(List<byte> bytes)
    {
        var result = new StringBuilder((bytes.Count * 8 + 4) / 5);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                result.Append(Base32Alphabet[(buffer >> bitsLeft) & 0x1f]);
            }
        }

        if (bitsLeft > 0)
            result.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1f]);

        return result.ToString();
    }

    private static byte[] Base32Decode(string code)
    {
        var bytes = new List<byte>(code.Length * 5 / 8);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var c in code)
        {
            var value = Base32Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (value < 0)
                throw new DeckCodeException($"Ongeldig teken '{c}' in de deck-code.");
            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                bytes.Add((byte)((buffer >> bitsLeft) & 0xff));
            }
        }

        if (bytes.Count == 0)
            throw new DeckCodeException("Deck-code is te kort om een deck te bevatten.");
        return [.. bytes];
    }

    /// <summary>Sequentiële lezer over de gedecodeerde bytes. Elke lees-actie
    /// voorbij het einde is een corrupte of afgekapte code en levert één
    /// consistente foutboodschap op (de bron gooit hier drie verschillende).</summary>
    private sealed class ByteReader(byte[] bytes)
    {
        private int _position;

        public byte ReadByte() =>
            _position < bytes.Length
                ? bytes[_position++]
                : throw new DeckCodeException("Deck-code is afgekapt of corrupt: onverwacht einde van de data.");

        public int ReadVarint()
        {
            var result = 0;
            var shift = 0;
            while (true)
            {
                var b = ReadByte();
                result |= (b & 0x7f) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
            }
        }
    }
}
