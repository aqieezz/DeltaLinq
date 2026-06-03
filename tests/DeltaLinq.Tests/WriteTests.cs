using DeltaLinq;
using Xunit;

namespace DeltaLinq.Tests;

public class WriteTests
{
    public class WriteRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public double Amount { get; set; }
        public bool Active { get; set; }
        public DateTime When { get; set; }
    }

    [SkippableFact]
    public async Task Append_creates_table_and_reads_back()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var dir = Path.Combine(Path.GetTempPath(), "deltalinq_write_" + Guid.NewGuid().ToString("N"));
        try
        {
            var when = new DateTime(2024, 3, 15, 9, 30, 0);
            var written = await DeltaTable.AppendAsync(dir, new[]
            {
                new WriteRow { Id = 1, Name = "alice", Amount = 10.5, Active = true, When = when },
                new WriteRow { Id = 2, Name = "bob", Amount = 20.0, Active = false, When = when.AddDays(1) },
            });
            Assert.Equal(2, written);

            var rows = await DeltaTable.Open<WriteRow>(dir).OrderBy(x => x.Id).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal("alice", rows[0].Name);
            Assert.Equal(10.5, rows[0].Amount, 3);
            Assert.True(rows[0].Active);
            Assert.False(rows[1].Active);
            Assert.Equal(when, rows[0].When); // DateTime round-trips exactly (UTC session)
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Second_append_adds_a_new_commit()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var dir = Path.Combine(Path.GetTempPath(), "deltalinq_write_" + Guid.NewGuid().ToString("N"));
        try
        {
            await DeltaTable.AppendAsync(dir, new[] { new WriteRow { Id = 1, Name = "a", Amount = 10.5, Active = true } });
            await DeltaTable.AppendAsync(dir, new[]
            {
                new WriteRow { Id = 2, Name = "b", Amount = 20.0, Active = false },
                new WriteRow { Id = 3, Name = "c", Amount = 5.0, Active = true },
            });

            Assert.Equal(3, await DeltaTable.Open<WriteRow>(dir).CountAsync());
            Assert.Equal(35.5, await DeltaTable.Open<WriteRow>(dir).SumAsync(x => x.Amount), 3);

            // two commit files (version 0 and version 1)
            var commits = Directory.GetFiles(Path.Combine(dir, "_delta_log"), "*.json");
            Assert.Equal(2, commits.Length);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
