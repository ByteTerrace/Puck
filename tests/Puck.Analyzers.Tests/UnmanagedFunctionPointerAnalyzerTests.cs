using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// Exercises <see cref="UnmanagedFunctionPointerAnalyzer"/> over small unsafe compilations, and pins the runtime
/// behavior its accept/refuse boundary rests on by calling real unmanaged function pointers.
/// </summary>
public sealed unsafe class UnmanagedFunctionPointerAnalyzerTests {
    private const string Id = "INTEROP001";
    private const string Shapes = """
        public enum VkResult {
            Success = 0,
        }

        public struct VkWin32SurfaceCreateInfoKhr {
            public int Type;
        }

        public struct Pair<T> {
            public T First;
            public T Second;
        }

        """;

    private struct Pair<T> where T : unmanaged {
        public T First;
        public T Second;
    }

    [UnmanagedCallersOnly]
    private static int ReadFirst(int* value) =>
        *value;
    private static int CallByPointer<T>(T* value) where T : unmanaged {
        var target = ((delegate* unmanaged<T*, int>)((nint)((delegate* unmanaged<int*, int>)(&ReadFirst))));

        return target(value);
    }
    private static int CallByPointerToGenericStruct<T>(Pair<T>* value) where T : unmanaged {
        var target = ((delegate* unmanaged<Pair<T>*, int>)((nint)((delegate* unmanaged<int*, int>)(&ReadFirst))));

        return target(value);
    }
    // The call INTEROP001 refuses, written here where the analyzer does not run so its runtime failure can be observed.
    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    private static int CallByReference<T>(in T value) where T : unmanaged {
        var target = ((delegate* unmanaged<in T, int>)((nint)((delegate* unmanaged<int*, int>)(&ReadFirst))));

        return target(in value);
    }
    private static int CallThroughPointerView<T>(delegate* unmanaged<in T, int> target, in T value) where T : unmanaged {
        var byAddress = ((delegate* unmanaged<void*, int>)target);

        fixed (T* address = &value) {
            return byAddress(address);
        }
    }
    private static AnalysisResult Run(string body) {
        var compilation = Harness.Compile(
            assemblyName: Harness.DefaultAssemblyName,
            sources: new SourceFile(
                Name: "Subject.cs",
                Text: (("namespace Subject.Assembly;\n\n" + Shapes) + body)
            )
        );

        return Harness.Analyze(
            analyzer: new UnmanagedFunctionPointerAnalyzer(),
            compilation: compilation.WithOptions(options: compilation.Options.WithAllowUnsafe(enabled: true))
        );
    }
    private static AnalysisResult RunClean(string body) {
        var result = Run(body: body);

        Assert.True(
            condition: result.CompilesCleanly,
            userMessage: result.CompilerErrorText
        );

        return result;
    }

    [Fact]
    public void AClosedCreateInfoInsideAGenericMethodIsAccepted() {
        var result = RunClean(body: """
            public static unsafe class Subject {
                public static VkResult Create<TCreateInfo>(nint proc, nint instance, in VkWin32SurfaceCreateInfoKhr createInfo) where TCreateInfo : unmanaged {
                    var create = (delegate* unmanaged[Cdecl]<nint, in VkWin32SurfaceCreateInfoKhr, nint, out nint, VkResult>)proc;

                    return create(instance, in createInfo, 0, out _);
                }
            }

            """);

        Assert.Empty(collection: result.Analyzer);
    }
    [Fact]
    public void AClosedGenericArgumentIsAccepted() {
        var result = RunClean(body: """
            public static unsafe class Subject {
                public static Pair<int> Call<T>(nint proc, Pair<int> pair) {
                    return ((delegate* unmanaged<Pair<int>, Pair<int>>)proc)(pair);
                }
            }

            """);

        Assert.Empty(collection: result.Analyzer);
    }
    [Fact]
    public void AManagedFunctionPointerMentioningATypeParameterIsAccepted() {
        var result = RunClean(body: """
            public static unsafe class Subject {
                public static T Call<T>(delegate*<in T, Span<T>, T> target, in T value) {
                    return target(in value, default);
                }
            }

            """);

        Assert.Empty(collection: result.Analyzer);
    }
    [Fact]
    public void ANestedFunctionPointerIsJudgedWhereItIsCalled() {
        var result = RunClean(body: """
            public static unsafe class Subject {
                public static void Call<T>(nint proc, T value) {
                    var outer = (delegate* unmanaged<delegate* unmanaged<in T, void>, void>)proc;
                    var inner = (delegate* unmanaged<in T, void>)proc;

                    outer(inner);
                    inner(in value);
                }
            }

            """);

        var diagnostic = result.Single(id: Id);

        Assert.Contains(
            expectedSubstring: "'delegate* unmanaged<in T, void>' throws MarshalDirectiveException",
            actualString: diagnostic.GetMessage(),
            comparisonType: StringComparison.Ordinal
        );
        Assert.Contains(
            expectedSubstring: "type parameter 'T' appears in parameter 1 ('in T')",
            actualString: diagnostic.GetMessage(),
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void APointerToATypeParameterIsAccepted() {
        var result = RunClean(body: """
            public static unsafe class Subject {
                public static VkResult Create<TCreateInfo>(nint proc, nint instance, TCreateInfo* createInfo) where TCreateInfo : unmanaged {
                    var create = (delegate* unmanaged[Cdecl]<nint, TCreateInfo*, Pair<TCreateInfo>*, nint, out nint, VkResult>)proc;

                    return create(instance, createInfo, null, 0, out _);
                }
            }

            """);

        Assert.Empty(collection: result.Analyzer);
    }
    [Fact]
    public void APointerToATypeParameterCallsThroughAtRunTime() {
        var value = 41;
        var pair = new Pair<int> {
            First = 42,
            Second = 43,
        };

        Assert.Equal(
            actual: CallByPointer(value: &value),
            expected: 41
        );
        Assert.Equal(
            actual: CallByPointerToGenericStruct(value: &pair),
            expected: 42
        );
    }
    [Fact]
    public void ATypeParameterAsAGenericArgumentInTheReturnTypeIsRefused() {
        var result = RunClean(body: """
            public static unsafe class Subject {
                public static Pair<T> Call<T>(nint proc) {
                    return ((delegate* unmanaged<nint, Pair<T>>)proc)(0);
                }
            }

            """);

        Assert.Contains(
            expectedSubstring: "type parameter 'T' appears in the return type ('Pair<T>')",
            actualString: result.Single(id: Id).GetMessage(),
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void ATypeParameterAsAnArrayElementIsRefused() {
        var result = RunClean(body: """
            public static unsafe class Subject {
                public static void Call<T>(nint proc, T[] values) {
                    ((delegate* unmanaged<T[], void>)proc)(values);
                }
            }

            """);

        Assert.Contains(
            expectedSubstring: "type parameter 'T' appears in parameter 1 ('T[]')",
            actualString: result.Single(id: Id).GetMessage(),
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void ATypeParameterByReferenceThrowsAtRunTime() {
        var exception = Assert.Throws<MarshalDirectiveException>(testCode: () => CallByReference(value: 41));

        Assert.Contains(
            expectedSubstring: "generic",
            actualString: exception.Message,
            comparisonType: StringComparison.OrdinalIgnoreCase
        );
    }
    [Fact]
    public void ATypeParameterInsideSpanIsRefused() {
        var result = RunClean(body: """
            public static unsafe class Subject {
                public static void Call<T>(nint proc, Span<T> values) {
                    ((delegate* unmanaged<Span<T>, void>)proc)(values);
                }
            }

            """);

        Assert.Contains(
            expectedSubstring: "type parameter 'T' appears in parameter 1 ('Span<T>')",
            actualString: result.Single(id: Id).GetMessage(),
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void ATypeParameterOfTheContainingTypeIsRefusedOnlyWhereTheTypeIsOpen() {
        var result = RunClean(body: """
            public unsafe sealed class Holder<T> {
                public delegate* unmanaged<ref T, void> ByReference;
                public delegate* unmanaged<Outer<T>.Inner, void> ByNestedType;

                public void Call(T value, Outer<T>.Inner inner) {
                    ByReference(ref value);
                    ByNestedType(inner);
                }
            }

            public sealed class Outer<T> {
                public struct Inner {
                    public int Value;
                }
            }

            public static unsafe class ClosedCaller {
                public static void Call(Holder<int> holder, int value, Outer<int>.Inner inner) {
                    holder.ByReference(ref value);
                    holder.ByNestedType(inner);
                }
            }

            """);

        var messages = result.WithId(id: Id).Select(selector: diagnostic => diagnostic.GetMessage()).OrderBy(
            comparer: StringComparer.Ordinal,
            keySelector: message => message
        ).ToArray();

        Assert.Equal(
            actual: messages.Length,
            expected: 2
        );
        Assert.Contains(
            expectedSubstring: "appears in parameter 1 ('Outer<T>.Inner')",
            actualString: messages[0],
            comparisonType: StringComparison.Ordinal
        );
        Assert.Contains(
            expectedSubstring: "appears in parameter 1 ('ref T')",
            actualString: messages[1],
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void EveryUnmanagedConventionAndModifierIsRefused() {
        var result = RunClean(body: """
            public static unsafe class Subject {
                public static void Call<T>(nint proc, T value) {
                    ((delegate* unmanaged<T, void>)proc)(value);
                    ((delegate* unmanaged[Stdcall]<ref T, void>)proc)(ref value);
                    ((delegate* unmanaged[Cdecl, SuppressGCTransition]<out T, void>)proc)(out value);
                }
            }

            """);

        Assert.Equal(
            actual: result.WithId(id: Id).Length,
            expected: 3
        );
    }
    [Fact]
    public void APointerViewOfAGenericSignatureCallsThroughAtRunTime() {
        var target = ((delegate* unmanaged<in int, int>)((nint)((delegate* unmanaged<int*, int>)(&ReadFirst))));

        Assert.Equal(
            actual: CallThroughPointerView(
                target: target,
                value: 41
            ),
            expected: 41
        );
    }
    [Fact]
    public void TheVulkanPointerViewIsAccepted() {
        var result = RunClean(body: """
            public static unsafe class Subject {
                public static VkResult Create<TCreateInfo>(
                    nint instance,
                    delegate* unmanaged[Cdecl]<nint, in TCreateInfo, nint, out nint, VkResult> createSurface,
                    in TCreateInfo createInfo,
                    out nint surfaceHandle
                ) where TCreateInfo : unmanaged {
                    var createSurfaceByAddress = ((delegate* unmanaged[Cdecl]<nint, void*, nint, nint*, VkResult>)createSurface);
                    nint createdHandle = 0;
                    VkResult result;

                    fixed (TCreateInfo* createInfoAddress = &createInfo) {
                        result = createSurfaceByAddress(instance, createInfoAddress, 0, &createdHandle);
                    }

                    surfaceHandle = createdHandle;

                    return result;
                }
            }

            """);

        Assert.Empty(collection: result.Analyzer);
    }
    [Fact]
    public void TheVulkanSurfaceShapeIsRefusedAtItsCall() {
        var result = RunClean(body: """
            public static unsafe class Subject {
                public static VkResult Create<TCreateInfo>(nint proc, nint instance, in TCreateInfo createInfo) where TCreateInfo : unmanaged {
                    var create = (delegate* unmanaged[Cdecl]<nint, in TCreateInfo, nint, out nint, VkResult>)proc;

                    return create(instance, in createInfo, 0, out _);
                }
            }

            """);

        var diagnostic = result.Single(id: Id);

        Assert.Equal(
            actual: diagnostic.Severity,
            expected: Microsoft.CodeAnalysis.DiagnosticSeverity.Error
        );
        Assert.StartsWith(
            actualString: diagnostic.Location.SourceTree!.ToString().Substring(startIndex: diagnostic.Location.SourceSpan.Start),
            expectedStartString: "create(instance, in createInfo",
            comparisonType: StringComparison.Ordinal
        );
        Assert.Contains(
            expectedSubstring: "type parameter 'TCreateInfo' appears in parameter 2 ('in TCreateInfo')",
            actualString: diagnostic.GetMessage(),
            comparisonType: StringComparison.Ordinal
        );
    }
}
