using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// The shell discovery follows Windows Terminal: WSL distributions come from the Lxss
/// registry, where Docker and Rancher keep a utility distribution that users must not see.
/// </summary>
public class ShellProfilesTests
{
    [Theory]
    [InlineData("docker-desktop", true)]
    [InlineData("docker-desktop-data", true)]
    [InlineData("rancher-desktop", true)]
    [InlineData("Ubuntu", false)]
    [InlineData("Debian", false)]
    [InlineData("Ubuntu-22.04", false)]
    public void UtilityDistributionsAreHidden(string name, bool utility) =>
        Assert.Equal(utility, ShellProfiles.IsUtilityDistribution(name));
}
