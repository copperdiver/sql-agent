namespace SqlAgent.Tests;

/// <summary>
/// The host writes two files into its own source directory at run time: launch-url.txt, which carries a
/// live launch token, and sqlagent.db, the local store. Neither belongs in the repository, and a phase B2
/// review run left both behind. A test rather than a habit, because the failure mode is silent until the
/// token is already in someone's history.
/// </summary>
public class RepoHygieneTests
{
    private static HashSet<string> Entries() =>
        File.ReadAllLines(RepoPaths.Find(".gitignore"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

    [Theory]
    [InlineData("launch-url.txt")]
    [InlineData("sqlagent.db*")]
    public void Run_time_artifacts_are_gitignored(string entry)
    {
        Assert.Contains(entry, Entries());
    }

    [Theory]
    [InlineData("sqlagent.db-journal")]
    [InlineData("sqlagent.db-wal")]
    [InlineData("sqlagent.db-shm")]
    public void The_stores_sidecar_files_are_covered_whatever_journal_mode_is_in_use(string file)
    {
        // Naming journal files one at a time is how sqlagent.db-journal came to be tracked-able: the
        // ignore list covered -wal and -shm, and the store runs in SQLite's default DELETE mode, whose
        // journal is none of those. The glob is asserted here through what it has to cover, so a future
        // narrowing back to named entries fails rather than quietly re-opening the same gap.
        var entries = Entries();
        Assert.True(
            entries.Contains(file) || entries.Contains("sqlagent.db*"),
            $"{file} is not covered by .gitignore.");
    }
}
