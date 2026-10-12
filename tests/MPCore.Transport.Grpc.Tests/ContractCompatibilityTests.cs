using System.Text.Json.Nodes;
using MPCore.Transport.Grpc.Tests.Contracts;

namespace MPCore.Transport.Grpc.Tests;

/// <summary>
/// The contract files of the command line tool and of the template (docs/contracts) hold what a consumer depends
/// on. A later contract is compatible with an earlier one when it states every fact the earlier one stated. These
/// tests hold the rule, the contract at the published baseline, and the seeded controls: each fault a consumer
/// would feel must be seen by the comparison, and an addition must ask for the file to be updated.
/// </summary>
public sealed class ContractCompatibilityTests
{
    [Theory]
    [InlineData("0.10.1", "0.10.2", false)]
    [InlineData("0.10.1", "0.11.0", true)]
    [InlineData("0.10.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("1.0.0", "1.1.0", false)]
    [InlineData("1.4.2", "2.0.0", true)]
    public void A_break_is_allowed_by_a_new_minor_version_before_1_0_and_by_a_new_major_version_after(string baseline, string declared, bool allowed) =>
        Assert.Equal(allowed, ContractFacts.BreakAllowed(Version.Parse(baseline), Version.Parse(declared)));

    [Theory]
    [InlineData("cli.json")]
    [InlineData("template.json")]
    public void A_contract_never_drops_or_changes_what_the_published_baseline_stated_unless_the_version_may_break_it(string file) =>
        ContractFacts.AssertNothingPublishedIsBroken(file);

    [Fact]
    public void A_removed_cli_option_is_a_break()
    {
        var published = Load("cli.json");
        var current = Load("cli.json");
        current["commands"]!["new backend"]!["options"]!.AsObject().Remove("skip-verify");

        var difference = ContractFacts.Compare(ContractFacts.Of(published), ContractFacts.Of(current));

        Assert.Contains("commands.new backend.options.skip-verify.kind=flag", difference.Removed);
        Assert.Empty(difference.Added);
        Assert.False(ContractFacts.BreakAllowed(Version.Parse("0.10.1"), Version.Parse("0.10.2")));
    }

    [Fact]
    public void A_renamed_template_parameter_is_a_break_and_a_new_name_is_an_addition()
    {
        var published = Load("template.json");
        var current = Load("template.json");
        var parameters = current["parameters"]!.AsObject();
        var renamed = parameters["securityAuthority"]!;
        parameters.Remove("securityAuthority");
        parameters["securityAuthorityUrl"] = renamed.DeepClone();

        var difference = ContractFacts.Compare(ContractFacts.Of(published), ContractFacts.Of(current));

        Assert.Contains("parameters.securityAuthority.datatype=text", difference.Removed);
        Assert.Contains("parameters.securityAuthorityUrl.datatype=text", difference.Added);
    }

    [Fact]
    public void A_removed_choice_a_changed_default_and_a_removed_exit_code_are_breaks()
    {
        var published = Load("cli.json");
        var current = Load("cli.json");
        var shape = current["commands"]!["new backend"]!["options"]!["shape"]!.AsObject();
        shape["choices"] = new JsonArray("service");
        shape["default"] = "modular-monolith";
        current["probes"]!.AsObject().Remove("new backend: the output directory is not empty");

        var difference = ContractFacts.Compare(ContractFacts.Of(published), ContractFacts.Of(current));

        Assert.Contains("commands.new backend.options.shape.choices[]=modular-monolith", difference.Removed);
        Assert.Contains("commands.new backend.options.shape.default=service", difference.Removed);
        Assert.Contains("probes.new backend: the output directory is not empty.exitCode=3", difference.Removed);
        Assert.Contains("commands.new backend.options.shape.default=modular-monolith", difference.Added);
    }

    [Fact]
    public void A_file_the_template_no_longer_generates_for_a_shape_is_a_break()
    {
        var published = Load("template.json");
        var current = Load("template.json");
        var common = current["files"]!["common"]!.AsArray();
        var dropped = common[0]!.GetValue<string>();
        common.RemoveAt(0);

        var difference = ContractFacts.Compare(ContractFacts.Of(published), ContractFacts.Of(current));

        Assert.Equal([$"files.common[]={dropped}"], difference.Removed);
    }

    [Fact]
    public void An_addition_widens_the_contract_and_asks_for_the_file_to_be_updated()
    {
        var recorded = Load("cli.json");
        var fromCode = Load("cli.json");
        fromCode["commands"]!["new backend"]!["options"]!.AsObject()["dry-run"] = new JsonObject { ["kind"] = "flag", ["required"] = false };

        var difference = ContractFacts.Compare(ContractFacts.Of(recorded), ContractFacts.Of(fromCode));

        Assert.Empty(difference.Removed);
        Assert.False(difference.IsEmpty, "an option the file does not record must fail the comparison of the file with the code");
        Assert.Contains("commands.new backend.options.dry-run.kind=flag", difference.Added);
    }

    [Fact]
    public void A_contract_file_states_the_facts_of_the_tool_and_of_the_template_that_the_comparison_reads()
    {
        // The comparison is only as good as the facts in the files: the ones a consumer depends on are there.
        var cli = ContractFacts.Of(Load("cli.json"));
        var template = ContractFacts.Of(Load("template.json"));

        Assert.Contains("commands.new backend.options.transport.required=true", cli);
        Assert.Contains("commands.new backend.options.shape.default=service", cli);
        Assert.Contains("commands.configure.options.project.required=true", cli);
        Assert.Contains("manifest.schemaVersion=4", cli);
        Assert.Contains("manifest.members.aiTooling=string", cli);
        Assert.Contains("probes.no arguments.exitCode=2", cli);
        Assert.Contains("parameters.transport.required=true", template);
        Assert.Contains("parameters.mpcoreVersion.default=<cohort version>", template);
        Assert.Contains(template, static fact => fact.StartsWith("files.by shape and transport.service/grpc.also[]=", StringComparison.Ordinal));
    }

    private static JsonNode Load(string file) => JsonNode.Parse(File.ReadAllText(ContractFacts.PathOf(file)))!;
}
