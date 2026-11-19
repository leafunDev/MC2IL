namespace MC2IL.Core.Tests;

public class DynamicWidthTests
{
    [SetUp]
    public void Setup()
    {
        MC2ILApi.ResetInternalState();
        TestGameLoader.LoadSimpleV106Game();
    }
    
    [Test]
    public void AssertTheGameLoadedProperly()
    {
        var context = MC2ILApi.CurrentAppContext!;
        
        //Yes, this is really dumb and stupid as a test. But it does actually test that the dynamic-width impl is correct (or we'd throw in setup)
        //and we can't actually check the calculation easily from here, because the index widths are only configured while actively loading the metadata.
        Assert.That(context, Is.Not.Null);
    }
}
