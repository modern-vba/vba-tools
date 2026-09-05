using VbaTools.Syntax;
using Xunit;

namespace VbaTools.Syntax.Tests;

public sealed class SyntaxOwnershipTests
{
    [Fact]
    public void SyntaxModelBelongsToTheProductNeutralAssemblyAndNamespace()
    {
        Assert.Equal("VbaTools.Syntax", typeof(VbaSyntaxTree).Assembly.GetName().Name);
        Assert.Equal("VbaTools.Syntax", typeof(VbaSyntaxTree).Namespace);
    }
}
