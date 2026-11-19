namespace MC2IL.Core.Tests;

public class MC2ILApiTests
{
    [Test]
    public void UnityVersionIsCorrectlyDeterminedFromGlobalGameManagers()
    {
        var version = MC2ILApi.DetermineUnityVersion(null, Paths.Simple2019Game.DataDirectory);
        Assert.That(version.Equals(2019, 4, 34));
    }
}
