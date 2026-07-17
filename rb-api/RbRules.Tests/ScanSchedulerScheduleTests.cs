using RbRules.Api;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>De periodieke jobplanning van de scheduler (#122-mechaniek,
/// declaratief sinds de #206-review): elke geplande naam moet in de
/// JobCatalog bestaan (zelfde vangnet als JobPathsTests voor padstappen) en
/// de changeconsolidatie (#206, review-fix finding 5) moet erin staan — de
/// uurlijkse scan maakt de duplicaten, dus de consolidatie mag niet alleen
/// van het handmatige ingest-pad afhangen.</summary>
public class ScanSchedulerScheduleTests
{
    [Fact]
    public void JobSchedules_ElkeNaamBestaatInDeJobCatalog()
    {
        foreach (var (jobName, _) in ScanScheduler.JobSchedules)
            Assert.NotNull(JobCatalog.Find(jobName));
    }

    [Fact]
    public void JobSchedules_BevatDeChangeconsolidatie()
    {
        var schedule = Assert.Single(
            ScanScheduler.JobSchedules, s => s.JobName == "consolidatechanges");
        // Zelfde ritme als de scan-tick (1u): consolidatie loopt de verse
        // changes van elke scan achterna — goedkoop dankzij poort + memo's.
        Assert.Equal(TimeSpan.FromHours(1), schedule.Window);
    }

    [Fact]
    public void JobSchedules_GeenDubbeleNamen()
    {
        var names = ScanScheduler.JobSchedules.Select(s => s.JobName).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }
}
