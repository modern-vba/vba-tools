using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.ComTypes;
using VbaDev.Infrastructure.References;
using Xunit;

namespace VbaDev.Tests;

public sealed class TypeLibElementDescriptorLayoutTests
{
    [Fact]
    public void DescriptorStrideMatchesTheNativeComArrayLayout()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ELEMDESC>());
        Assert.Equal(IntPtr.Size * 4, TypeLibElementDescriptorLayout.Stride);
    }

    [Fact]
    public void DescriptorPointerAdvancesByTheNativeStride()
    {
        var start = new IntPtr(0x1000);

        Assert.Equal(start, TypeLibElementDescriptorLayout.At(start, 0));
        Assert.Equal(IntPtr.Add(start, IntPtr.Size * 4),
            TypeLibElementDescriptorLayout.At(start, 1));
        Assert.Equal(IntPtr.Add(start, IntPtr.Size * 8),
            TypeLibElementDescriptorLayout.At(start, 2));
    }
}
