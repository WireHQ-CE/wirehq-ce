using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace WireHQ.Application.UnitTests.Persistence;

/// <summary>
/// Guards the Community Edition migration chain's structural invariants from inside the normal test suite —
/// the ones that must survive long after whoever built it has moved on (docs/37 §5, §7 T9).
/// </summary>
public sealed class CeMigrationChainTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WireHQ.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("could not locate the repo root");
    }

    private static string ChainDir() =>
        Path.Combine(RepoRoot(), "ce", "overlay", "src", "WireHQ.Infrastructure", "Persistence", "Migrations");

    /// <summary>
    /// The migration bodies only: not the <c>.Designer.cs</c> partials, and not the model snapshot — which
    /// lives in the same directory, is not a migration, and would otherwise inflate every count and get
    /// scanned by the content assertions below.
    /// </summary>
    private static string[] ChainMigrations() =>
        Directory.Exists(ChainDir())
            ? [.. Directory.GetFiles(ChainDir(), "*.cs")
                .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
                .Where(f => Path.GetFileName(f) != "ApplicationDbContextModelSnapshot.cs")
                .Order()]
            : [];

    /// <summary>
    /// T9 — a periodic prompt to reconsider the legacy watermark probe in <c>ApplicationDbContextInitializer</c>.
    ///
    /// <b>Corrected premise.</b> This originally asserted the probe was dead code once the chain reached 3
    /// migrations, reasoning that "no live database can still be sitting on the bare frozen baseline". That is
    /// wrong, and it firing two days after being written is what showed it: chain length says nothing about
    /// whether any database has upgraded. A CE installed at v0.90.1 and never updated still has
    /// <c>applied == {baseline}</c> however many migrations we later ship — and that is precisely the population
    /// the probe protects.
    ///
    /// The probe genuinely dies when the SUPPORTED UPGRADE FLOOR moves past v0.90.x, which is an operator
    /// decision and unknowable from the repo. So this is deliberately a prompt at a threshold, not an assertion
    /// of dead code: when it fires, decide — retire the probe and delete this, or raise the threshold.
    /// </summary>
    [Fact]
    public void The_legacy_watermark_probe_is_reconsidered_as_the_chain_grows()
    {
        var migrations = ChainMigrations();
        if (migrations.Length == 0)
        {
            return; // the generated CE tree has no ce/ directory
        }

        migrations.Length.Should().BeLessThan(8, because:
            "the CE chain has grown enough to be worth asking whether any install can still be sitting on the bare " +
            "frozen baseline. If the supported upgrade floor has moved past v0.90.x, retire the legacy watermark " +
            "probe in ApplicationDbContextInitializer (EnsureMigrationPreconditionsAsync, state 4) and delete this " +
            "test. If it has NOT moved, raise this threshold — do not delete the probe. See docs/37 §3.4, Slice 4.");
    }

    /// <summary>The baseline is the chain's root forever — it sorts before every real yyyyMMddHHmmss id.</summary>
    [Fact]
    public void The_frozen_baseline_is_the_first_migration()
    {
        var migrations = ChainMigrations();
        if (migrations.Length == 0)
        {
            return;
        }

        Path.GetFileName(migrations[0]).Should().Be("00000000000000_InitialCreate.cs");
    }

    /// <summary>
    /// The CE audit_logs must stay unpartitioned: partitioning is raw SQL, invisible to the EF model, so the
    /// initializer and the retention sweeper both detect a plain table and skip partition management.
    /// </summary>
    [Fact]
    public void The_chain_never_partitions()
    {
        foreach (var file in ChainMigrations())
        {
            UpBody(File.ReadAllText(file)).Should().NotContainEquivalentOf("PARTITION BY",
                because: $"{Path.GetFileName(file)} would break the CE's unpartitioned audit_logs assumption");
        }
    }

    /// <summary>
    /// Out-of-model schema DDL would make the CI schema-equivalence proof permanently noisy and force an
    /// "accepted diff" file. Data-only Sql() keeps that invariant crisp. Mirrors guardrail 5e-iv.
    /// </summary>
    [Fact]
    public void Hand_written_sql_in_the_chain_carries_data_only()
    {
        foreach (var file in ChainMigrations())
        {
            var up = UpBody(File.ReadAllText(file));
            foreach (Match m in Regex.Matches(up, @"migrationBuilder\.Sql\((.*?)\);", RegexOptions.Singleline))
            {
                Regex.IsMatch(m.Groups[1].Value, @"\b(CREATE|ALTER|DROP|TRUNCATE)\b", RegexOptions.IgnoreCase)
                    .Should().BeFalse(because:
                        $"{Path.GetFileName(file)} carries schema DDL inside Sql() — data-only is permitted (docs/17 §7.1)");
            }
        }
    }

    /// <summary>
    /// EF's differ has no rename detection, so a renamed property scaffolds DropColumn + AddColumn and EF only
    /// warns. From v0.91.0 this chain runs against live self-hosted databases that take no automatic
    /// pre-migration backup, so a drop must be a deliberate, in-diff decision. Mirrors guardrail 5e-v.
    /// </summary>
    [Fact]
    public void Destructive_operations_carry_a_written_justification()
    {
        foreach (var file in ChainMigrations())
        {
            var up = UpBody(File.ReadAllText(file));
            if (!Regex.IsMatch(up, @"Drop(Column|Table|Schema|PrimaryKey|ForeignKey|Index)"))
            {
                continue;
            }

            up.Should().Contain("ce-destructive:", because:
                $"{Path.GetFileName(file)} drops schema in its Up() without a '// ce-destructive: <reason>' justification");
        }
    }

    /// <summary>Down() legitimately drops everything, so every scan above is Up()-scoped.</summary>
    private static string UpBody(string source)
    {
        var start = source.IndexOf("void Up(", StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var end = source.IndexOf("void Down(", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }
}
