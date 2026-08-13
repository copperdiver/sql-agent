using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>
/// Bucketing is arithmetic on calendar days, which is exactly the kind of code that looks obviously
/// right and is wrong at 00:30. A chat from 23:00 yesterday is "Yesterday" even though it is ninety
/// minutes old; a chat from 01:00 today is "Today" even though it is thirty minutes old. Nothing here
/// is about elapsed hours.
/// </summary>
public class ChatHistoryGroupingTests
{
    // A fixed local "now" with an awkward time of day: late enough that subtracting hours crosses
    // midnight backwards, early enough that adding them crosses forwards.
    private static readonly DateTime NowLocal = new(2026, 8, 12, 0, 30, 0, DateTimeKind.Local);

    private static HistoryBucket Bucket(DateTime local) =>
        ChatHistoryGrouping.BucketOf(local.ToUniversalTime(), NowLocal);

    [Fact]
    public void A_chat_from_ninety_minutes_ago_is_Yesterday_not_Today()
    {
        // 23:00 on the 11th, seen at 00:30 on the 12th. Elapsed-time arithmetic ("less than 24 hours is
        // today") gets this wrong, and it is the most common way this feature ships broken.
        Assert.Equal(HistoryBucket.Yesterday, Bucket(new DateTime(2026, 8, 11, 23, 0, 0, DateTimeKind.Local)));
    }

    [Fact]
    public void A_chat_from_thirty_minutes_ago_is_Today()
    {
        Assert.Equal(HistoryBucket.Today, Bucket(new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Local)));
    }

    [Theory]
    // Day offsets from "now" and the bucket each must land in. The interesting values are the edges:
    // 1 is the last Yesterday, 2 the first Previous7Days, 7 the last of it, 8 the first Previous30Days,
    // 30 the last of that, 31 the first Older.
    [InlineData(0, HistoryBucket.Today)]
    [InlineData(1, HistoryBucket.Yesterday)]
    [InlineData(2, HistoryBucket.Previous7Days)]
    [InlineData(7, HistoryBucket.Previous7Days)]
    [InlineData(8, HistoryBucket.Previous30Days)]
    [InlineData(30, HistoryBucket.Previous30Days)]
    [InlineData(31, HistoryBucket.Older)]
    [InlineData(400, HistoryBucket.Older)]
    public void Day_offsets_land_in_the_documented_buckets(int daysAgo, HistoryBucket expected)
    {
        Assert.Equal(expected, Bucket(NowLocal.Date.AddDays(-daysAgo).AddHours(9)));
    }

    [Fact]
    public void A_chat_dated_in_the_future_is_Today_rather_than_falling_off_the_end()
    {
        // Clock skew, a store copied from another machine, or a timezone change while the host runs. A
        // negative day difference must not fall through to Older, which would bury the newest chat at
        // the bottom of the list. NowLocal.AddHours(6) would not actually test this: 00:30 + 6h is 06:30
        // on the same calendar day, so `.Date - .Date` is still 0, not negative — the `<= 0` arm's `<= `
        // half would cover it just as well as its `0` half, and a regression to `days < 0 => Older` would
        // slip past unnoticed. AddDays(2) lands on a later calendar day than NowLocal, making the day
        // difference genuinely negative.
        Assert.Equal(HistoryBucket.Today, Bucket(NowLocal.AddDays(2)));
    }

    [Fact]
    public void Groups_come_back_newest_first_with_their_chats_newest_first_and_no_empty_group()
    {
        var chats = new[]
        {
            new ChatSummary(Guid.NewGuid(), "old", NowLocal.Date.AddDays(-40).ToUniversalTime()),
            new ChatSummary(Guid.NewGuid(), "today early", NowLocal.Date.AddMinutes(1).ToUniversalTime()),
            new ChatSummary(Guid.NewGuid(), "today late", NowLocal.Date.AddMinutes(20).ToUniversalTime()),
        };

        var groups = ChatHistoryGrouping.Group(chats, NowLocal);

        Assert.Equal([HistoryBucket.Today, HistoryBucket.Older], groups.Select(g => g.Bucket));
        Assert.Equal(["today late", "today early"], groups[0].Chats.Select(c => c.Title));
        // Yesterday and the two "previous" buckets are absent entirely rather than present and empty: a
        // heading with nothing under it reads as a rendering bug.
        Assert.DoesNotContain(groups, g => g.Chats.Count == 0);
    }

    [Fact]
    public void Every_bucket_has_a_label_to_render()
    {
        // The sidebar renders Label directly, so a bucket added later without one would render an empty
        // heading rather than fail a build.
        foreach (var bucket in Enum.GetValues<HistoryBucket>())
            Assert.False(string.IsNullOrWhiteSpace(ChatHistoryGrouping.LabelOf(bucket)));
    }
}
