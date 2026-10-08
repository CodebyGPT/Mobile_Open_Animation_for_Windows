// Why this file exists
// ====================
//
// This project targets .NET Framework 4.8 on purpose. That is the version of .NET that
// ships as a component of Windows 10 (1903 and later) and Windows 11, so the built .exe
// runs on any of those machines with nothing for the user to install. A .NET 8 or 9 build
// would need a 50-60 MB runtime installed first, which is a hard sell for a small tray
// utility.
//
// The price of targeting 4.8 is a handful of APIs that were added later, in .NET Core or
// .NET 5+. This file holds the replacements. It is deliberately small and each item says
// which API it stands in for, so that a reviewer can check the claim rather than take it
// on trust.
//
// It is NOT a compatibility layer for other operating systems or other runtimes: the
// program is Win32 interop and GDI rendering throughout, and every call it makes exists in
// .NET Framework.

using System;

// ---------------------------------------------------------------------------------------
// Compiler-required attributes that .NET Framework does not ship
// ---------------------------------------------------------------------------------------
//
// C# features `record` and `init`-only setters compile down to a constructor call on
// IsExternalInit. C# 11 `required` members compile down to attributes on the type and the
// member. In both cases the compiler only needs the type to exist; the runtime never looks
// at it, which is why declaring it here is the entire fix rather than a workaround.
//
// This is the officially documented approach for using those features on .NET Framework.

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field
                  | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;
        public string FeatureName { get; }
        public bool IsOptional { get; init; }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}

// ---------------------------------------------------------------------------------------
// Missing BCL APIs
// ---------------------------------------------------------------------------------------

namespace Moa
{
    internal static class Compat
    {
        /// <summary>
        /// Stands in for Math.Clamp, which is .NET Core 2.0 / .NET Standard 2.1 and therefore
        /// absent from .NET Framework (which implements .NET Standard 2.0).
        /// </summary>
        public static int Clamp(int value, int min, int max)
            => value < min ? min : value > max ? max : value;

        /// <inheritdoc cref="Clamp(int,int,int)"/>
        public static double Clamp(double value, double min, double max)
            => value < min ? min : value > max ? max : value;

        /// <summary>
        /// Stands in for Environment.ProcessPath, which is .NET 6+. On .NET Framework the
        /// entry assembly's location is the path of the running .exe, which is what every
        /// caller here wants (to read our own icon out of the executable).
        /// </summary>
        public static string ExePath
            => System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";

        /// <summary>
        /// Stands in for Environment.TickCount64, which is .NET Core 3.0+ and absent from
        /// .NET Framework (Environment.TickCount is a 32-bit value that wraps after ~24.9
        /// days, which is no good for deadlines). A monotonic millisecond clock built on
        /// Stopwatch, which the program already uses for its latency measurement.
        /// </summary>
        public static long TickCount64
            => System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000);

        /// <summary>Milliseconds elapsed since a Stopwatch.GetTimestamp() reading.</summary>
        public static double ElapsedMs(long startTimestamp)
            => (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp) * 1000.0
               / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>
        /// Stands in for OperatingSystem.IsWindowsVersionAtLeast, which is .NET 5+. Used to
        /// tell Windows 11 (build 22000+) from Windows 10, because 11 rounds the corners of
        /// top-level windows and 10 does not.
        /// </summary>
        public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;
    }
}
