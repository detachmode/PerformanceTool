using System.Text.RegularExpressions;
using Beesharp.Services;

namespace Beesharp.Cli.Tests;

/// <summary>
/// <c>docs/cli.md</c> is the reference for every command and option: each command the CLI's own <c>--help</c> lists has a
/// section there, and each option it prints is described in that section. A new command or option fails here until it is documented.
/// </summary>
public sealed partial class CliDocumentationTests
{
    // Options every "runs" subcommand takes; the docs describe them once, above the subcommands.
    private static readonly string[] SharedRunsOptions = ["--url", "--json", "--help"];

    [GeneratedRegex(@"(?<![\w-])--[a-z][a-z-]*")] private static partial Regex LongOption();
    [GeneratedRegex(@"^\s{2}([a-z]+)\s", RegexOptions.Multiline)] private static partial Regex SubcommandLine();

    private static readonly Lazy<string> Docs = new(() => File.ReadAllText(Path.Combine(RepoRoot(), "docs", "cli.md")));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Beesharp.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Beesharp.slnx not found above " + AppContext.BaseDirectory);
    }

    private static async Task<string> HelpAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApp.RunAsync(args, output, error, CancellationToken.None);
        Assert.True(exit == ExitCodes.Ok, $"'beesharp {string.Join(' ', args)}' exited with {exit}: {error}");
        return output.ToString();
    }

    /// <summary>The text of the docs section that starts with <paramref name="heading"/>, up to the next heading of the same or a higher level.</summary>
    private static string Section(string heading)
    {
        var docs = Docs.Value;
        var start = docs.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"docs/cli.md has no section '{heading}'.");
        var level = heading.TakeWhile(c => c == '#').Count();
        var next = Regex.Match(docs[(start + heading.Length)..], $@"^#{{1,{level}}} ", RegexOptions.Multiline);
        return next.Success ? docs.Substring(start, heading.Length + next.Index) : docs[start..];
    }

    /// <summary>Each option in <paramref name="help"/> needs a row in an options table of the section (an example alone is not a description).</summary>
    private static void AssertDocumented(string help, string section, string sectionName, IEnumerable<string>? sharedOptions = null, bool inTable = true)
    {
        var shared = (sharedOptions ?? []).ToHashSet();
        var where = inTable ? @"^\|\s*`[^`\n]*" : @"`[^`\n]*";
        var missing = LongOption().Matches(help).Select(m => m.Value).Distinct()
            .Where(option => !shared.Contains(option) && !Regex.IsMatch(section, $@"{where}{Regex.Escape(option)}(?![\w-])", RegexOptions.Multiline))
            .ToList();
        Assert.True(missing.Count == 0, $"docs/cli.md, section '{sectionName}', does not describe: {string.Join(", ", missing)}");
    }

    private static async Task<List<string>> RunsSubcommandsAsync()
    {
        var help = await HelpAsync("runs", "--help");
        var block = help[help.IndexOf("Subcommands:", StringComparison.Ordinal)..];
        block = block[..block.IndexOf("\n\n", StringComparison.Ordinal)];
        return SubcommandLine().Matches(block).Select(m => m.Groups[1].Value).ToList();
    }

    [Fact]
    public async Task Every_option_of_beesharp_run_is_documented()
    {
        AssertDocumented(await HelpAsync("run", "--help"), Section("## `beesharp run`"), "beesharp run");
    }

    [Fact]
    public async Task Every_runs_subcommand_has_its_own_section()
    {
        var subcommands = await RunsSubcommandsAsync();
        Assert.True(subcommands.Count >= 8, $"Expected the runs help to list its subcommands, found [{string.Join(", ", subcommands)}].");
        var missing = subcommands.Where(sub => !Docs.Value.Contains($"### `beesharp runs {sub}`", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, $"docs/cli.md has no '### `beesharp runs <subcommand>`' section for: {string.Join(", ", missing)}");
    }

    [Fact]
    public async Task Every_option_of_every_runs_subcommand_is_documented()
    {
        AssertDocumented(string.Join(' ', SharedRunsOptions), Section("## `beesharp runs`"), "beesharp runs", inTable: false);
        foreach (var sub in await RunsSubcommandsAsync())
            AssertDocumented(await HelpAsync("runs", sub, "--help"), Section($"### `beesharp runs {sub}`"), $"beesharp runs {sub}", SharedRunsOptions);
    }

    [Fact]
    public void Every_option_of_the_embedded_hosts_run_command_is_documented()
    {
        AssertDocumented(HeadlessArgumentParser.HostRunUsage, Section("## The embedded host's `run` command"), "embedded host run");
    }

    [Fact]
    public async Task The_overview_lists_every_command()
    {
        var overview = Section("## Commands at a glance");
        Assert.Contains("beesharp run ", overview);
        var missing = (await RunsSubcommandsAsync()).Where(sub => !overview.Contains($"beesharp runs {sub}", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, $"'Commands at a glance' in docs/cli.md does not list: {string.Join(", ", missing)}");
    }
}
