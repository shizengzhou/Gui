using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Gui.Cli;

/// <summary>
/// Enumerates the installed families through DirectWrite, the same engine the terminal draws
/// with, and asks it whether a face is monospaced — the answer the settings page shows, so the
/// picker lists exactly the families that page offers. DirectWrite is reached without a wrapper
/// because the CLI ships no packages: the factory is created by DWriteCreateFactory and the few
/// methods needed are called through the vtable.
/// </summary>
internal static class DirectWriteFonts
{
    private static readonly Guid FactoryId = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
    private static readonly Guid FontFace1Id = new("a71efdb4-9fdb-4838-ad90-cfc3be8c3daf");

    private const int SharedFactory = 0;
    private const int WeightNormal = 400;
    private const int StretchNormal = 5;
    private const int StyleNormal = 0;

    /// <summary>
    /// Every family DirectWrite knows, with the monospaced answer for its regular face and the
    /// weight/slant of each face in the family.
    /// </summary>
    public static List<(string Family, bool Monospaced, List<(int Weight, int Style)> Faces)> Families()
    {
        var families = new List<(string, bool, List<(int, int)>)>();
        IntPtr factory = IntPtr.Zero;
        try
        {
            Guid iid = FactoryId;
            if (DWriteCreateFactory(SharedFactory, ref iid, out factory) < 0 || factory == IntPtr.Zero)
                return families;

            IntPtr collection = IntPtr.Zero;
            if (Call<GetSystemFontCollectionFn>(factory, 3)(factory, out collection, 0) < 0 ||
                collection == IntPtr.Zero)
                return families;

            try
            {
                int count = Call<GetCountFn>(collection, 3)(collection);
                for (int i = 0; i < count; i++)
                {
                    IntPtr family = IntPtr.Zero;
                    if (Call<GetFontFamilyFn>(collection, 4)(collection, i, out family) < 0 ||
                        family == IntPtr.Zero)
                        continue;

                    try
                    {
                        string name = FamilyName(family);
                        if (name.Length == 0) continue;
                        families.Add((name, IsMonospaced(family), Faces(family)));
                    }
                    finally
                    {
                        Marshal.Release(family);
                    }
                }
            }
            finally
            {
                Marshal.Release(collection);
            }
        }
        catch (Exception)
        {
            // A DirectWrite that cannot be reached leaves an empty list; the caller falls back.
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }

        return families;
    }

    /// <summary>The weight and slant of each face the family lists.</summary>
    private static List<(int Weight, int Style)> Faces(IntPtr family)
    {
        var faces = new List<(int, int)>();
        int count = Call<GetCountFn>(family, 4)(family);
        for (int i = 0; i < count; i++)
        {
            IntPtr font = IntPtr.Zero;
            if (Call<GetFontFn>(family, 5)(family, i, out font) < 0 || font == IntPtr.Zero) continue;
            try
            {
                int weight = Call<GetValueFn>(font, 4)(font);
                int style = Call<GetValueFn>(font, 6)(font);
                if (!faces.Contains((weight, style))) faces.Add((weight, style));
            }
            finally
            {
                Marshal.Release(font);
            }
        }
        return faces;
    }

    private static string FamilyName(IntPtr family)
    {
        IntPtr strings = IntPtr.Zero;
        if (Call<GetFamilyNamesFn>(family, 6)(family, out strings) < 0 || strings == IntPtr.Zero)
            return string.Empty;

        try
        {
            int count = Call<GetCountFn>(strings, 3)(strings);
            if (count <= 0) return string.Empty;

            int index = 0;
            if (Call<FindLocaleNameFn>(strings, 4)(strings, "en-us", out int found, out int exists) >= 0 &&
                exists != 0)
                index = found;

            if (Call<GetStringLengthFn>(strings, 7)(strings, index, out int length) < 0) return string.Empty;

            var buffer = new StringBuilder(length + 1);
            if (Call<GetStringFn>(strings, 8)(strings, index, buffer, buffer.Capacity) < 0)
                return string.Empty;
            return buffer.ToString();
        }
        finally
        {
            Marshal.Release(strings);
        }
    }

    /// <summary>
    /// Whether the family's regular face declares itself monospaced. IDWriteFontFace1 answers
    /// from the font itself, which is why it counts Nerd Font builds GDI calls proportional.
    /// </summary>
    private static bool IsMonospaced(IntPtr family)
    {
        IntPtr font = IntPtr.Zero;
        if (Call<GetFirstMatchingFontFn>(family, 7)(family, WeightNormal, StretchNormal, StyleNormal,
                out font) < 0 || font == IntPtr.Zero)
            return false;

        try
        {
            IntPtr face = IntPtr.Zero;
            if (Call<CreateFontFaceFn>(font, 13)(font, out face) < 0 || face == IntPtr.Zero)
                return false;

            try
            {
                Guid iid = FontFace1Id;
                if (Marshal.QueryInterface(face, in iid, out IntPtr face1) < 0 || face1 == IntPtr.Zero)
                    return false;

                try
                {
                    // IDWriteFontFace1::IsMonospacedFont: base IDWriteFontFace has fifteen
                    // methods (through ReleaseFontTable), so the fifth IDWriteFontFace1 method
                    // lands on slot 22.
                    return Call<IsMonospacedFontFn>(face1, 22)(face1) != 0;
                }
                finally
                {
                    Marshal.Release(face1);
                }
            }
            finally
            {
                Marshal.Release(face);
            }
        }
        finally
        {
            Marshal.Release(font);
        }
    }

    private static T Call<T>(IntPtr instance, int slot) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(instance);
        IntPtr method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSystemFontCollectionFn(IntPtr self, out IntPtr collection, int checkForUpdates);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCountFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFontFamilyFn(IntPtr self, int index, out IntPtr family);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFontFn(IntPtr self, int index, out IntPtr font);

    /// <summary>A zero-argument method that returns an enum value (weight, stretch, style).</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetValueFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFamilyNamesFn(IntPtr self, out IntPtr strings);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFirstMatchingFontFn(IntPtr self, int weight, int stretch, int style,
        out IntPtr font);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateFontFaceFn(IntPtr self, out IntPtr face);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FindLocaleNameFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string locale,
        out int index, out int exists);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetStringLengthFn(IntPtr self, int index, out int length);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetStringFn(IntPtr self, int index,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder buffer, int size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsMonospacedFontFn(IntPtr self);

    [DllImport("dwrite.dll")]
    private static extern int DWriteCreateFactory(int factoryType, ref Guid iid, out IntPtr factory);
}
