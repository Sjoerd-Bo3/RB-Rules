FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# Geen expliciete solution-arg: .NET 10 genereert RbRules.slnx en
# restore/test vinden die zelf (expliciet 'RbRules.sln' brak de image-build).
RUN dotnet restore
RUN dotnet test --no-restore
RUN dotnet publish RbRules.Api/RbRules.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "RbRules.Api.dll"]
