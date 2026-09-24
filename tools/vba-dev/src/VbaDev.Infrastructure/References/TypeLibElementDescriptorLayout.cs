using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.ComTypes;

namespace VbaDev.Infrastructure.References;

internal static class TypeLibElementDescriptorLayout
{
    // ELEMDESC and its nested COM descriptor types contain only pointers and 16-bit values.
    // Their managed size therefore matches the native FUNCDESC parameter-array stride.
    internal static int Stride { get; } = Unsafe.SizeOf<ELEMDESC>();

    internal static IntPtr At(IntPtr start, int index) =>
        IntPtr.Add(start, checked(index * Stride));
}
