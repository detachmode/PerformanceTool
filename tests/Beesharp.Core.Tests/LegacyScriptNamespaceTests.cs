using Beesharp.Services;

namespace Beesharp.Core.Tests;

public class LegacyScriptNamespaceTests
{
    [Fact]
    public void Old_usings_and_qualified_names_are_mapped_on_the_same_line()
    {
        var code = "using PerformanceTool.Services.Scenarios;\nusing PerformanceTool.Services.Environments;\nvar x = new PerformanceTool.Services.Runtime.RuntimeValue();\nvar s = \"PerformanceToolbox\";";
        var up = LegacyScriptNamespaces.Upgrade(code);
        Assert.Equal("using Beesharp.Services.Scenarios;\nusing Beesharp.Services.Environments;\nvar x = new Beesharp.Services.Runtime.RuntimeValue();\nvar s = \"PerformanceToolbox\";", up);
        Assert.Equal(code.Split('\n').Length, up.Split('\n').Length);
    }
}
