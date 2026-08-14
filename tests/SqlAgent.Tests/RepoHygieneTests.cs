namespace SqlAgent.Tests;

/// <summary>
/// The host writes two files into its own source directory at run time: launch-url.txt, which carries a
/// live launch token, and sqlagent.db, the local store. Neither belongs in the repository, and a phase B2
/// review run left both behind. A test rather than a habit, because the failure mode is silent until the
/// token is already in someone's history.
/// </summary>
public class RepoHygieneTests
{
    [Theory]
    [InlineData("launch-url.txt")]
    [InlineData("sqlagent.db")]
    [InlineData("sqlagent.db-wal")]
    [InlineData("sqlagent.db-shm")]
    public void Run_time_artifacts_are_gitignored(string entry)
    {
        var lines = File.ReadAllLines(RepoPaths.Find(".gitignore"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(entry, lines);
    }
}
