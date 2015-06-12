﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using ProprietaryTestResources = Microsoft.CodeAnalysis.Test.Resources.Proprietary;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;
using System.Reflection.PortableExecutable;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    public class ReferenceManagerTests : CSharpTestBase
    {
        private static readonly CSharpCompilationOptions s_signedDll = TestOptions.ReleaseDll.
            WithCryptoKeyFile(SigningTestHelpers.KeyPairFile).
            WithStrongNameProvider(new SigningTestHelpers.VirtualizedStrongNameProvider(ImmutableArray.Create<string>()));

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void VersionUnification_SymbolUsed()
        {
            // Identity: C, Version=1.0.0.0, Culture=neutral, PublicKeyToken=374d0c2befcd8cc9
            var v1 = AssemblyMetadata.CreateFromImage(TestResources.SymbolsTests.General.C1).GetReference(display: "C, V1");

            // Identity: C, Version=2.0.0.0, Culture=neutral, PublicKeyToken=374d0c2befcd8cc9
            var v2 = AssemblyMetadata.CreateFromImage(TestResources.SymbolsTests.General.C2).GetReference(display: "C, V2");

            var refV1 = CreateCompilationWithMscorlib("public class D : C { }", new[] { v1 }, assemblyName: "refV1");
            var refV2 = CreateCompilationWithMscorlib("public class D : C { }", new[] { v2 }, assemblyName: "refV2");

            // reference asks for a lower version than available:
            var testRefV1 = CreateCompilationWithMscorlib("public class E : D { }", new MetadataReference[] { new CSharpCompilationReference(refV1), v2 }, assemblyName: "testRefV1");

            // reference asks for a higher version than available:
            var testRefV2 = CreateCompilationWithMscorlib("public class E : D { }", new MetadataReference[] { new CSharpCompilationReference(refV2), v1 }, assemblyName: "testRefV2");

            // TODO (tomat): we should display paths rather than names "refV1" and "C"

            testRefV1.VerifyDiagnostics(
                // warning CS1701: 
                // Assuming assembly reference 'C, Version=1.0.0.0, Culture=neutral, PublicKeyToken=374d0c2befcd8cc9' 
                // used by 'refV1' matches identity 'C, Version=2.0.0.0, Culture=neutral, PublicKeyToken=374d0c2befcd8cc9' of 'C', you may need to supply runtime policy
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin, "D").WithArguments(
                    "C, Version=1.0.0.0, Culture=neutral, PublicKeyToken=374d0c2befcd8cc9",
                    "refV1",
                    "C, Version=2.0.0.0, Culture=neutral, PublicKeyToken=374d0c2befcd8cc9",
                    "C"));

            // TODO (tomat): we should display paths rather than names "refV2" and "C"

            testRefV2.VerifyDiagnostics(
                // error CS1705: Assembly 'refV2' with identity 'refV2, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null' 
                // uses 'C, Version=2.0.0.0, Culture=neutral, PublicKeyToken=374d0c2befcd8cc9' which has a higher version than referenced assembly 
                // 'C' with identity 'C, Version=1.0.0.0, Culture=neutral, PublicKeyToken=374d0c2befcd8cc9'
                Diagnostic(ErrorCode.ERR_AssemblyMatchBadVersion, "D").WithArguments(
                    "refV2",
                    "refV2, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
                    "C, Version=2.0.0.0, Culture=neutral, PublicKeyToken=374d0c2befcd8cc9",
                    "C",
                    "C, Version=1.0.0.0, Culture=neutral, PublicKeyToken=374d0c2befcd8cc9"));
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        [WorkItem(546080, "DevDiv")]
        public void VersionUnification_SymbolNotUsed()
        {
            var v1 = MetadataReference.CreateFromImage(TestResources.SymbolsTests.General.C1);
            var v2 = MetadataReference.CreateFromImage(TestResources.SymbolsTests.General.C2);

            var refV1 = CreateCompilationWithMscorlib("public class D : C { }", new[] { v1 });
            var refV2 = CreateCompilationWithMscorlib("public class D : C { }", new[] { v2 });

            // reference asks for a lower version than available:
            var testRefV1 = CreateCompilationWithMscorlib("public class E { }", new MetadataReference[] { new CSharpCompilationReference(refV1), v2 });

            // reference asks for a higher version than available:
            var testRefV2 = CreateCompilationWithMscorlib("public class E { }", new MetadataReference[] { new CSharpCompilationReference(refV2), v1 });

            testRefV1.VerifyDiagnostics();
            testRefV2.VerifyDiagnostics();
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void VersionUnification_MultipleVersions()
        {
            string sourceLibV1 = @"
[assembly: System.Reflection.AssemblyVersion(""1.0.0.0"")]
public class C {}
";

            var libV1 = CreateCompilationWithMscorlib(
                sourceLibV1,
                assemblyName: "Lib",
                options: s_signedDll);

            string sourceLibV2 = @"
[assembly: System.Reflection.AssemblyVersion(""2.0.0.0"")]
public class C {}
";

            var libV2 = CreateCompilationWithMscorlib(
                sourceLibV2,
                assemblyName: "Lib",
                options: s_signedDll);

            string sourceLibV3 = @"
[assembly: System.Reflection.AssemblyVersion(""3.0.0.0"")]
public class C {}
";

            var libV3 = CreateCompilationWithMscorlib(
                sourceLibV3,
                assemblyName: "Lib",
                options: s_signedDll);

            string sourceRefLibV2 = @"
using System.Collections.Generic;

[assembly: System.Reflection.AssemblyVersion(""2.0.0.0"")]

public class R { public C Field; }
";

            var refLibV2 = CreateCompilationWithMscorlib(
               sourceRefLibV2,
               assemblyName: "RefLibV2",
               references: new[] { new CSharpCompilationReference(libV2) },
               options: s_signedDll);

            string sourceMain = @"
public class M
{
    public void F()
    {
        var x = new R();                        
        System.Console.WriteLine(x.Field);
    }
}
";
            // higher version should be preferred over lower version regardless of the order of the references

            var main13 = CreateCompilationWithMscorlib(
               sourceMain,
               assemblyName: "Main",
               references: new[]
               {
                   new CSharpCompilationReference(libV1),
                   new CSharpCompilationReference(libV3),
                   new CSharpCompilationReference(refLibV2)
               });

            // TODO (tomat): we should display paths rather than names "RefLibV2" and "Lib"

            main13.VerifyDiagnostics(
                // warning CS1701: Assuming assembly reference 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV2' matches identity 'Lib, Version=3.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin).WithArguments(
                    "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2",
                    "RefLibV2",
                    "Lib, Version=3.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2",
                    "Lib"));

            var main31 = CreateCompilationWithMscorlib(
               sourceMain,
               assemblyName: "Main",
               references: new[]
               {
                   new CSharpCompilationReference(libV3),
                   new CSharpCompilationReference(libV1),
                   new CSharpCompilationReference(refLibV2)
               });

            // TODO (tomat): we should display paths rather than names "RefLibV2" and "Lib"

            main31.VerifyDiagnostics(
                // warning CS1701: Assuming assembly reference 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV2' matches identity 'Lib, Version=3.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin).WithArguments(
                    "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2",
                    "RefLibV2",
                    "Lib, Version=3.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2",
                    "Lib"));
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        [WorkItem(529808, "DevDiv"), WorkItem(530246, "DevDiv")]
        public void VersionUnification_UseSiteWarnings()
        {
            string sourceLibV1 = @"
[assembly: System.Reflection.AssemblyVersion(""1.0.0.0"")]

public class C {}
public delegate void D();
public interface I {}
";

            var libV1 = CreateCompilationWithMscorlib(
                sourceLibV1,
                assemblyName: "Lib",
                options: s_signedDll);

            string sourceLibV2 = @"
[assembly: System.Reflection.AssemblyVersion(""2.0.0.0"")]
public class C {}
public delegate void D();
public interface I {}
";

            var libV2 = CreateCompilationWithMscorlib(
                sourceLibV2,
                assemblyName: "Lib",
                options: s_signedDll);

            string sourceRefLibV1 = @"
using System.Collections.Generic;

[assembly: System.Reflection.AssemblyVersion(""1.0.0.0"")]

public class R 
{
    public R(C c) {}

    public C Field;

    public C Property { get; set; }

    public int this[C arg]
    {
        get { return 0; } 
        set {}
    }

    public event D Event;

    public List<C> Method1()
    {
        return null;
    }

    public void Method2(List<List<C>> c) { }
    public void GenericMethod<T>() where T : I { }
}

public class S1 : List<C>
{
   public class Inner {}
}

public class S2 : I {}

public class GenericClass<T>
    where T : I
{
   public class S {}
}
";

            var refLibV1 = CreateCompilationWithMscorlib(
               sourceRefLibV1,
               assemblyName: "RefLibV1",
               references: new[] { new CSharpCompilationReference(libV1) },
               options: s_signedDll);

            string sourceX = @"
[assembly: System.Reflection.AssemblyVersion(""2.0.0.0"")]

public class P : Q {} 
public class Q : S2 {} 
";

            var x = CreateCompilationWithMscorlib(
               sourceX,
               assemblyName: "X",
               references: new[] { new CSharpCompilationReference(refLibV1), new CSharpCompilationReference(libV1) },
               options: s_signedDll);

            string sourceMain = @"
public class M
{
    public void F()
    {
        var c = new C();                        // ok
        var r = new R(null);                    // error: C in parameter
        var f = r.Field;                        // error: C in type
        var a = r.Property;                     // error: C in return type
        var b = r[c];                           // error: C in parameter
        r.Event += () => {};                    // error: C in type
        var m = r.Method1();                    // error: ~> C in return type
        r.Method2(null);                        // error: ~> C in parameter
        r.GenericMethod<OKImpl>();              // error: ~> I in constraint
        var g = new GenericClass<OKImpl>.S();   // error: ~> I in constraint -- should report only once, for GenericClass<OKImpl>, not again for S.
        var s1 = new S1();                      // error: ~> C in base
        var s2 = new S2();                      // error: ~> I in implements
        var s3 = new S1.Inner();                // error: ~> C in base -- should only report once, for S1, not again for Inner.
        var e = new P();                        // error: P -> Q -> S2 ~> I in implements  
    }
}

public class Z : S2                             // error: S2 ~> I in implements 
{
}

public class OKImpl : I
{
}
";
            var main = CreateCompilationWithMscorlib(
               sourceMain,
               assemblyName: "Main",
               references: new[] { new CSharpCompilationReference(refLibV1), new CSharpCompilationReference(libV2), new CSharpCompilationReference(x) });

            // TODO (tomat): we should display paths rather than names "RefLibV1" and "Lib"

            main.VerifyDiagnostics(
                // (23,18): warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV1' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                // public class Z : S2                             // error: S2 ~> I in implements 
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin, "S2").WithArguments("Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "RefLibV1", "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "Lib"),
                // (7,21): warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV1' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                //         var r = new R(null);                    // error: C in parameter
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin, "R").WithArguments("Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "RefLibV1", "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "Lib"),
                // warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV1' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin).WithArguments("Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "RefLibV1", "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "Lib"),
                // warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV1' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin).WithArguments("Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "RefLibV1", "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "Lib"),
                // (10,17): warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV1' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                //         var b = r[c];                           // error: C in parameter
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin, "r[c]").WithArguments("Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "RefLibV1", "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "Lib"),
                // warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV1' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin).WithArguments("Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "RefLibV1", "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "Lib"),
                // warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV1' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin).WithArguments("Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "RefLibV1", "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "Lib"),
                // (12,17): warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV1' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                //         var m = r.Method1();                    // error: ~> C in return type
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin, "r.Method1").WithArguments("Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "RefLibV1", "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "Lib"),
                // (13,9): warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV1' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                //         r.Method2(null);                        // error: ~> C in parameter
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin, "r.Method2").WithArguments("Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "RefLibV1", "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "Lib"),
                // (14,9): warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV1' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                //         r.GenericMethod<OKImpl>();              // error: ~> I in constraint
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin, "r.GenericMethod<OKImpl>").WithArguments("Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "RefLibV1", "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "Lib"),
                // warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV1' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin).WithArguments("Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "RefLibV1", "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "Lib"),
                // warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV1' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin).WithArguments("Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "RefLibV1", "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "Lib"),
                // warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV1' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin).WithArguments("Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "RefLibV1", "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "Lib"),
                // warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV1' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin).WithArguments("Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "RefLibV1", "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "Lib"),
                // warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'X' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin).WithArguments("Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "X", "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "Lib"));

            CompileAndVerify(main, emitters: TestEmitters.CCI, validator: (assembly, _) =>
            {
                var reader = assembly.GetMetadataReader();
                List<string> refs = new List<string>();
                foreach (var assemblyRef in reader.AssemblyReferences)
                {
                    var row = reader.GetAssemblyReference(assemblyRef);
                    refs.Add(reader.GetString(row.Name) + " " + row.Version.Major + "." + row.Version.Minor);
                }

                // Dev11 adds "Lib 1.0" to the references, we don't (see DevDiv #15580)
                AssertEx.SetEqual(new[] { "mscorlib 4.0", "RefLibV1 1.0", "Lib 2.0", "X 2.0" }, refs);
            },
            // PE verification would need .config file with Lib v1 -> Lib v2 binding redirect 
            verify: false);
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        [WorkItem(546080, "DevDiv")]
        public void VersionUnification_UseSiteDiagnostics_Multiple()
        {
            string sourceA1 = @"
[assembly: System.Reflection.AssemblyVersion(""1.0.0.0"")]
public class A {}
";

            var a1 = CreateCompilationWithMscorlib(
                sourceA1,
                assemblyName: "A",
                options: s_signedDll);

            string sourceA2 = @"
[assembly: System.Reflection.AssemblyVersion(""2.0.0.0"")]
public class A {}
";

            var a2 = CreateCompilationWithMscorlib(
                sourceA2,
                assemblyName: "A",
                options: s_signedDll);

            string sourceB1 = @"
[assembly: System.Reflection.AssemblyVersion(""1.0.0.0"")]
public class B {}
";

            var b1 = CreateCompilationWithMscorlib(
                sourceB1,
                assemblyName: "B",
                options: s_signedDll);

            string sourceB2 = @"
[assembly: System.Reflection.AssemblyVersion(""2.0.0.0"")]
public class B {}
";

            var b2 = CreateCompilationWithMscorlib(
                sourceB2,
                assemblyName: "B",
                options: s_signedDll);

            string sourceRefA1B2 = @"
using System.Collections.Generic;

[assembly: System.Reflection.AssemblyVersion(""1.0.0.0"")]

public class R 
{
    public Dictionary<A, B> Dict = new Dictionary<A, B>();
    public void Foo(A a, B b) {}
}
";

            var refA1B2 = CreateCompilationWithMscorlib(
               sourceRefA1B2,
               assemblyName: "RefA1B2",
               references: new[] { new CSharpCompilationReference(a1), new CSharpCompilationReference(b2) },
               options: s_signedDll);

            string sourceMain = @"
public class M
{
    public void F()
    {
        var r = new R();
        System.Console.WriteLine(r.Dict);   // warning & error
        r.Foo(null, null);                  // warning & error
    }
}
";
            var main = CreateCompilationWithMscorlib(
               sourceMain,
               assemblyName: "Main",
               references: new[] { new CSharpCompilationReference(refA1B2), new CSharpCompilationReference(a2), new CSharpCompilationReference(b1) });

            // TODO (tomat): we should display paths rather than names "RefLibV1" and "Lib"

            // TODO (tomat): this should include 2 warnings:

            main.VerifyDiagnostics(
                // error CS1705: Assembly 'RefA1B2' with identity 'RefA1B2, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' uses
                // 'B, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' which has a higher version than referenced assembly 'B'
                // with identity 'B, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2'
                Diagnostic(ErrorCode.ERR_AssemblyMatchBadVersion).WithArguments(
                    "RefA1B2",
                    "RefA1B2, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2",
                    "B, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2",
                    "B",
                    "B, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2"),

                // (8,9): error CS1705: Assembly 'RefA1B2' with identity 'RefA1B2, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' uses 
                // 'B, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' which has a higher version than referenced assembly 'B'
                // with identity 'B, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2'
                Diagnostic(ErrorCode.ERR_AssemblyMatchBadVersion, "r.Foo").WithArguments(
                    "RefA1B2",
                    "RefA1B2, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2",
                    "B, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2",
                    "B",
                    "B, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2"));
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void VersionUnification_UseSiteDiagnostics_OptionalAttributes()
        {
            string sourceLibV1 = @"
[assembly: System.Reflection.AssemblyVersion(""1.0.0.0"")]

namespace System.Reflection
{
    [AttributeUsage(AttributeTargets.Assembly, Inherited = false)]
    public sealed class AssemblyVersionAttribute : Attribute
    {
        public AssemblyVersionAttribute(string version) {}
        public string Version { get; set; }
    }
}

public class CGAttribute : System.Attribute { }
";

            var libV1 = CreateCompilation(
                sourceLibV1,
                assemblyName: "Lib",
                references: new[] { MinCorlibRef },
                options: s_signedDll);

            libV1.VerifyDiagnostics();

            string sourceLibV2 = @"
[assembly: System.Reflection.AssemblyVersion(""2.0.0.0"")]

namespace System.Reflection
{
    [AttributeUsage(AttributeTargets.Assembly, Inherited = false)]
    public sealed class AssemblyVersionAttribute : Attribute
    {
        public AssemblyVersionAttribute(string version) {}
        public string Version { get; set; }
    }
}

public class CGAttribute : System.Attribute { }
";

            var libV2 = CreateCompilation(
                sourceLibV2,
                assemblyName: "Lib",
                references: new[] { MinCorlibRef },
                options: s_signedDll);

            libV2.VerifyDiagnostics();

            string sourceRefLibV1 = @"
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, Inherited = true)]
    public sealed class CompilerGeneratedAttribute : CGAttribute { }
}
";

            var refLibV1 = CreateCompilation(
               sourceRefLibV1,
               assemblyName: "RefLibV1",
               references: new MetadataReference[] { MinCorlibRef, new CSharpCompilationReference(libV1) },
               options: s_signedDll);

            refLibV1.VerifyDiagnostics();

            string sourceMain = @"
public class C
{
    public int P { get; set; }   // error: backing field is marked by CompilerGeneratedAttribute, whose base type is in the unified assembly
}
";
            var main = CreateCompilation(
               sourceMain,
               assemblyName: "Main",
               references: new MetadataReference[] { MinCorlibRef, new CSharpCompilationReference(refLibV1), new CSharpCompilationReference(libV2) });

            // Dev11 reports warning since the base type of CompilerGeneratedAttribute is in unified assembly.
            // Roslyn doesn't report any use-site diagnostics for optional attributes, it just ignores them

            main.VerifyDiagnostics();
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void VersionUnification_SymbolEquality()
        {
            string sourceLibV1 = @"
using System.Reflection;
[assembly: AssemblyVersion(""1.0.0.0"")]
public interface I {}
";

            var libV1 = CreateCompilationWithMscorlib(
                sourceLibV1,
                assemblyName: "Lib",
                options: s_signedDll);

            string sourceLibV2 = @"
using System.Reflection;
[assembly: AssemblyVersion(""2.0.0.0"")]
public interface I {}
";

            var libV2 = CreateCompilationWithMscorlib(
                sourceLibV2,
                assemblyName: "Lib",
                options: s_signedDll);

            string sourceRefLibV1 = @"
using System.Reflection;
[assembly: AssemblyVersion(""1.0.0.0"")]
public class C : I 
{ 
}
";

            var refLibV1 = CreateCompilationWithMscorlib(
               sourceRefLibV1,
               assemblyName: "RefLibV1",
               references: new[] { new CSharpCompilationReference(libV1) },
               options: s_signedDll);

            string sourceMain = @"
public class M 
{
    public void F() 
    {
        I x = new C();
    }
}
";
            var main = CreateCompilationWithMscorlib(
               sourceMain,
               assemblyName: "Main",
               references: new[] { new CSharpCompilationReference(refLibV1), new CSharpCompilationReference(libV2) });

            // TODO (tomat): we should display paths rather than names "RefLibV1" and "Lib"

            main.VerifyDiagnostics(
                // warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' 
                // used by 'RefLibV1' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin).WithArguments(
                    "Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2",
                    "RefLibV1",
                    "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2",
                    "Lib"));
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        [WorkItem(546752, "DevDiv")]
        public void VersionUnification_NoPiaMissingCanonicalTypeSymbol()
        {
            string sourceLibV1 = @"
[assembly: System.Reflection.AssemblyVersion(""1.0.0.0"")]
public class A {}
";

            var libV1 = CreateCompilationWithMscorlib(
                sourceLibV1,
                assemblyName: "Lib",
                options: s_signedDll);

            string sourceLibV2 = @"
[assembly: System.Reflection.AssemblyVersion(""2.0.0.0"")]
public class A {}
";

            var libV2 = CreateCompilationWithMscorlib(
                sourceLibV2,
                assemblyName: "Lib",
                options: s_signedDll);

            string sourceRefLibV1 = @"
using System.Runtime.InteropServices;

public class B : A
{
    public void M(IB i) { }
}

[ComImport]
[Guid(""F79F0037-0874-4EE3-BC45-158EDBA3ABA3"")]
[TypeIdentifier]
public interface IB
{
}
";

            var refLibV1 = CreateCompilationWithMscorlib(
               sourceRefLibV1,
               assemblyName: "RefLibV1",
               references: new[] { new CSharpCompilationReference(libV1) },
               options: TestOptions.ReleaseDll);

            string sourceMain = @"
public class Test
{
    static void Main()
    {
        B b = new B();
        b.M(null);
    }
}
";

            // NOTE: We won't get a nopia type unless we use a PE reference (i.e. source won't work).
            var main = CreateCompilationWithMscorlib(
               sourceMain,
               assemblyName: "Main",
               references: new MetadataReference[] { MetadataReference.CreateFromImage(refLibV1.EmitToArray()), new CSharpCompilationReference(libV2) },
               options: TestOptions.ReleaseExe);

            // TODO (tomat): we should display paths rather than names "RefLibV1" and "Lib"

            main.VerifyDiagnostics(
                // warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV1' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin).WithArguments("Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "RefLibV1", "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "Lib"),
                // warning CS1701: Assuming assembly reference 'Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'RefLibV1' matches identity 'Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'Lib', you may need to supply runtime policy
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin).WithArguments("Lib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "RefLibV1", "Lib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "Lib"),
                // (7,9): error CS1748: Cannot find the interop type that matches the embedded interop type 'IB'. Are you missing an assembly reference?
                //         b.M(null);
                Diagnostic(ErrorCode.ERR_NoCanonicalView, "b.M").WithArguments("IB"));
        }

        [WorkItem(546525, "DevDiv")]
        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void AssemblyReferencesWithAliases()
        {
            var source =
@"extern alias SysCore;
using System.Linq;

namespace Microsoft.TeamFoundation.WebAccess.Common
{
    public class CachedRegistry
    {
        public static void Main(string[] args)
        {
            System.Console.Write('k');
        }
    }
}";
            var tree = Parse(source);
            var r1 = AssemblyMetadata.CreateFromImage(ProprietaryTestResources.NetFX.v4_0_30319.System_Core).GetReference(filePath: @"c:\temp\aa.dll", display: "System.Core.v4_0_30319.dll");
            var r2 = AssemblyMetadata.CreateFromImage(ProprietaryTestResources.NetFX.v4_0_30319.System_Core).GetReference(filePath: @"c:\temp\aa.dll", display: "System.Core.v4_0_30319.dll");
            var r2_SysCore = r2.WithAliases(new[] { "SysCore" });

            var compilation = CreateCompilation(new List<SyntaxTree> { tree }, new[] { MscorlibRef, r1, r2_SysCore }, new CSharpCompilationOptions(OutputKind.ConsoleApplication), "Test");
            CompileAndVerify(compilation, expectedOutput: "k");
        }

        [WorkItem(545062, "DevDiv")]
        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void DuplicateReferences()
        {
            CSharpCompilation c;
            string source;

            var r1 = AssemblyMetadata.CreateFromImage(TestResources.SymbolsTests.General.C1).GetReference(filePath: @"c:\temp\a.dll", display: "R1");
            var r2 = AssemblyMetadata.CreateFromImage(TestResources.SymbolsTests.General.C1).GetReference(filePath: @"c:\temp\a.dll", display: "R2");
            var rFoo = r2.WithAliases(new[] { "foo" });
            var rBar = r2.WithAliases(new[] { "bar" });
            var rEmbed = r1.WithEmbedInteropTypes(true);

            source = @"
class D { }
";



            c = CreateCompilationWithMscorlib(source, new[] { r1, r2 });
            Assert.Null(c.GetReferencedAssemblySymbol(r1));
            Assert.NotNull(c.GetReferencedAssemblySymbol(r2));
            c.VerifyDiagnostics();

            source = @"
class D : C { }
            ";

            c = CreateCompilationWithMscorlib(source, new[] { r1, r2 });
            Assert.Null(c.GetReferencedAssemblySymbol(r1));
            Assert.NotNull(c.GetReferencedAssemblySymbol(r2));
            c.VerifyDiagnostics();

            c = CreateCompilationWithMscorlib(source, new[] { rFoo, r2 });
            Assert.Null(c.GetReferencedAssemblySymbol(rFoo));
            Assert.NotNull(c.GetReferencedAssemblySymbol(r2));
            AssertEx.SetEqual(new[] { "foo", "global" }, c.ExternAliases);
            c.VerifyDiagnostics();

            // 2 aliases for the same path, aliases not used to qualify name
            c = CreateCompilationWithMscorlib(source, new[] { rFoo, rBar });
            Assert.Null(c.GetReferencedAssemblySymbol(rFoo));
            Assert.NotNull(c.GetReferencedAssemblySymbol(rBar));
            AssertEx.SetEqual(new[] { "foo", "bar" }, c.ExternAliases);

            c.VerifyDiagnostics(
                // (2,11): error CS0246: The type or namespace name 'C' could not be found (are you missing a using directive or an assembly reference?)
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "C").WithArguments("C"));

            source = @"
class D : C { }
            ";

            // /l and /r with the same path
            c = CreateCompilationWithMscorlib(source, new[] { rFoo, rEmbed });
            Assert.Null(c.GetReferencedAssemblySymbol(rFoo));
            Assert.NotNull(c.GetReferencedAssemblySymbol(rEmbed));

            c.VerifyDiagnostics(
                // error CS1760: Assemblies 'R1' and 'R2' refer to the same metadata but only one is a linked reference (specified using /link option); consider removing one of the references.
                Diagnostic(ErrorCode.ERR_AssemblySpecifiedForLinkAndRef).WithArguments("R1", "R2"),
                // error CS1747: Cannot embed interop types from assembly 'C, Version=1.0.0.0, Culture=neutral, PublicKeyToken=374d0c2befcd8cc9' because it is missing the 'System.Runtime.InteropServices.GuidAttribute' attribute.
                Diagnostic(ErrorCode.ERR_NoPIAAssemblyMissingAttribute).WithArguments("C, Version=1.0.0.0, Culture=neutral, PublicKeyToken=374d0c2befcd8cc9", "System.Runtime.InteropServices.GuidAttribute"),
                // error CS1759: Cannot embed interop types from assembly 'C, Version=1.0.0.0, Culture=neutral, PublicKeyToken=374d0c2befcd8cc9' because it is missing either the 'System.Runtime.InteropServices.ImportedFromTypeLibAttribute' attribute or the 'System.Runtime.InteropServices.PrimaryInteropAssemblyAttribute' attribute.
                Diagnostic(ErrorCode.ERR_NoPIAAssemblyMissingAttributes).WithArguments("C, Version=1.0.0.0, Culture=neutral, PublicKeyToken=374d0c2befcd8cc9", "System.Runtime.InteropServices.ImportedFromTypeLibAttribute", "System.Runtime.InteropServices.PrimaryInteropAssemblyAttribute"),
                // (2,11): error CS1752: Interop type 'C' cannot be embedded. Use the applicable interface instead.
                // class D : C { }
                Diagnostic(ErrorCode.ERR_NewCoClassOnLink, "C").WithArguments("C"));

            source = @"
extern alias foo;
extern alias bar;

public class D : foo::C { }
public class E : bar::C { }
";
            // 2 aliases for the same path, aliases used
            c = CreateCompilationWithMscorlib(source, new[] { rFoo, rBar });
            Assert.Null(c.GetReferencedAssemblySymbol(rFoo));
            Assert.NotNull(c.GetReferencedAssemblySymbol(rBar));
            c.VerifyDiagnostics();
        }

        // "<path>\x\y.dll" -> "<path>\x\..\x\y.dll"
        private static string MakeEquivalentPath(string path)
        {
            string[] parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar);
            Debug.Assert(parts.Length >= 3);

            int dir = parts.Length - 2;
            List<string> newParts = new List<string>(parts);
            newParts.Insert(dir, "..");
            newParts.Insert(dir, parts[dir]);
            return newParts.Join(Path.DirectorySeparatorChar.ToString());
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void DuplicateAssemblyReferences_EquivalentPath()
        {
            string p1 = Temp.CreateFile().WriteAllBytes(TestResources.SymbolsTests.General.MDTestLib1).Path;
            string p2 = MakeEquivalentPath(p1);
            string p3 = MakeEquivalentPath(p2);

            var r1 = MetadataReference.CreateFromFile(p1);
            var r2 = MetadataReference.CreateFromFile(p2);
            SyntaxTree t1, t2, t3;

            var compilation = CSharpCompilation.Create("foo",
                syntaxTrees: new[]
                {
                    t1 = Parse("#r \"" + p2 + "\"", options: TestOptions.Script),
                    t2 = Parse("#r \"" + p3 + "\"", options: TestOptions.Script),
                    t3 = Parse("#r \"Foo\"", options: TestOptions.Script),
                },
                references: new MetadataReference[] { MscorlibRef, r1, r2 },
                options: TestOptions.ReleaseDll.
                    WithMetadataReferenceResolver(new AssemblyReferenceResolver(
                        new MappingReferenceResolver(assemblyNames: new Dictionary<string, string> { { "Foo", p3 } }),
                        MetadataFileReferenceProvider.Default))
            );

            // no diagnostics expected, all duplicate references should be ignored as they all refer to the same file:
            compilation.VerifyDiagnostics();

            var refs = compilation.ExternalReferences;
            Assert.Equal(3, refs.Length);
            Assert.Equal(MscorlibRef, refs[0]);
            Assert.Equal(r1, refs[1]);
            Assert.Equal(r2, refs[2]);

            // All #r's resolved are represented in directive references.
            var dirRefs = compilation.DirectiveReferences;
            Assert.Equal(2, dirRefs.Length);

            var as1 = compilation.GetReferencedAssemblySymbol(r2);
            Assert.Equal("MDTestLib1", as1.Identity.Name);

            // r1 is a dup of r2:
            Assert.Null(compilation.GetReferencedAssemblySymbol(r1));

            var rd1 = t1.GetCompilationUnitRoot().GetReferenceDirectives().Single();
            var rd2 = t2.GetCompilationUnitRoot().GetReferenceDirectives().Single();
            var rd3 = t3.GetCompilationUnitRoot().GetReferenceDirectives().Single();

            var dr1 = compilation.GetDirectiveReference(rd1) as PortableExecutableReference;
            var dr2 = compilation.GetDirectiveReference(rd2) as PortableExecutableReference;
            var dr3 = compilation.GetDirectiveReference(rd3) as PortableExecutableReference;

            Assert.Equal(MetadataImageKind.Assembly, dr1.Properties.Kind);
            Assert.Equal(MetadataImageKind.Assembly, dr2.Properties.Kind);
            Assert.Equal(MetadataImageKind.Assembly, dr3.Properties.Kind);

            Assert.True(dr1.Properties.Aliases.IsEmpty);
            Assert.True(dr2.Properties.Aliases.IsEmpty);
            Assert.True(dr3.Properties.Aliases.IsEmpty);

            Assert.False(dr1.Properties.EmbedInteropTypes);
            Assert.False(dr2.Properties.EmbedInteropTypes);
            Assert.False(dr3.Properties.EmbedInteropTypes);

            // the paths come from the resolver:
            Assert.Equal(p2, dr1.FilePath);
            Assert.Equal(p3, dr2.FilePath);
            Assert.Equal(p3, dr3.FilePath);
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void DuplicateModuleReferences_EquivalentPath()
        {
            var dir = Temp.CreateDirectory();
            string p1 = dir.CreateFile("netModule1.netmodule").WriteAllBytes(TestResources.SymbolsTests.netModule.netModule1).Path;
            string p2 = MakeEquivalentPath(p1);

            var m1 = MetadataReference.CreateFromFile(p1, new MetadataReferenceProperties(MetadataImageKind.Module));
            var m2 = MetadataReference.CreateFromFile(p2, new MetadataReferenceProperties(MetadataImageKind.Module));

            var compilation = CSharpCompilation.Create("foo", options: TestOptions.ReleaseDll,
                references: new MetadataReference[] { m1, m2 });

            // We don't deduplicate references based on file path on the compilation level.
            // The host (command line compiler and msbuild workspace) is responsible for such de-duplication, if needed.

            compilation.VerifyDiagnostics(
                // error CS8015: Module 'netModule1.netmodule' is already defined in this assembly. Each module must have a unique filename.
                Diagnostic(ErrorCode.ERR_NetModuleNameMustBeUnique).WithArguments("netModule1.netmodule"),
                // netModule1.netmodule: error CS0101: The namespace '<global namespace>' already contains a definition for 'Class1'
                Diagnostic(ErrorCode.ERR_DuplicateNameInNS).WithArguments("Class1", "<global namespace>"),
                // netModule1.netmodule: error CS0101: The namespace 'NS1' already contains a definition for 'Class4'
                Diagnostic(ErrorCode.ERR_DuplicateNameInNS).WithArguments("Class4", "NS1"),
                // netModule1.netmodule: error CS0101: The namespace 'NS1' already contains a definition for 'Class8'
                Diagnostic(ErrorCode.ERR_DuplicateNameInNS).WithArguments("Class8", "NS1"));

            var mods = compilation.Assembly.Modules.ToArray();
            Assert.Equal(3, mods.Length);

            Assert.NotNull(compilation.GetReferencedModuleSymbol(m1));
            Assert.NotNull(compilation.GetReferencedModuleSymbol(m2));
        }

        /// <summary>
        /// Two metadata files with the same strong identity referenced twice, with embedInteropTypes=true and embedInteropTypes=false.
        /// </summary>
        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void DuplicateAssemblyReferences_EquivalentStrongNames_Metadata()
        {
            var ref1 = AssemblyMetadata.CreateFromImage(TestResources.SymbolsTests.General.C2).GetReference(embedInteropTypes: true, filePath: @"R:\A\MTTestLib1.dll");
            var ref2 = AssemblyMetadata.CreateFromImage(TestResources.SymbolsTests.General.C2).GetReference(embedInteropTypes: false, filePath: @"R:\B\MTTestLib1.dll");

            var c = CreateCompilationWithMscorlib("class C {}", new[] { ref1, ref2 });
            c.VerifyDiagnostics(
                // error CS1760: Assemblies 'R:\B\MTTestLib1.dll' and 'R:\A\MTTestLib1.dll' refer to the same metadata but only one is a linked reference (specified using /link option); consider removing one of the references.
                Diagnostic(ErrorCode.ERR_AssemblySpecifiedForLinkAndRef).WithArguments(@"R:\B\MTTestLib1.dll", @"R:\A\MTTestLib1.dll"));
        }

        /// <summary>
        /// Two compilations with the same strong identity referenced twice, with embedInteropTypes=true and embedInteropTypes=false.
        /// </summary>
        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void DuplicateAssemblyReferences_EquivalentStrongNames_Compilations()
        {
            var sourceLib = @"
[assembly: System.Reflection.AssemblyVersion(""1.0.0.0"")]
public interface I {}";

            var lib1 = CreateCompilationWithMscorlib(sourceLib, options: s_signedDll, assemblyName: "Lib");
            var lib2 = CreateCompilationWithMscorlib(sourceLib, options: s_signedDll, assemblyName: "Lib");

            var ref1 = lib1.ToMetadataReference(embedInteropTypes: true);
            var ref2 = lib2.ToMetadataReference(embedInteropTypes: false);

            var c = CreateCompilationWithMscorlib("class C {}", new[] { ref1, ref2 });
            c.VerifyDiagnostics(
                // error CS1760: Assemblies 'Lib' and 'Lib' refer to the same metadata but only one is a linked reference (specified using /link option); consider removing one of the references.
                Diagnostic(ErrorCode.ERR_AssemblySpecifiedForLinkAndRef).WithArguments("Lib", "Lib"));
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void DuplicateAssemblyReferences_EquivalentName()
        {
            string p1 = Temp.CreateFile().WriteAllBytes(ProprietaryTestResources.NetFX.v4_0_30319.System_Core).Path;
            string p2 = Temp.CreateFile().CopyContentFrom(p1).Path;

            var r1 = MetadataReference.CreateFromFile(p1);
            var r2 = MetadataReference.CreateFromFile(p2);

            var compilation = CSharpCompilation.Create("foo", references: new[] { r1, r2 });

            var refs = compilation.Assembly.Modules.Select(module => module.GetReferencedAssemblies()).ToArray();
            Assert.Equal(1, refs.Length);
            Assert.Equal(1, refs[0].Length);
        }

        /// <summary>
        /// Two Framework identities with unified versions.
        /// </summary>
        [ClrOnlyFact(ClrOnlyReason.Signing)]
        [WorkItem(546026, "DevDiv"), WorkItem(546169, "DevDiv")]
        public void CS1703ERR_DuplicateImport()
        {
            var p1 = Temp.CreateFile().WriteAllBytes(ProprietaryTestResources.NetFX.v4_0_30319.System).Path;
            var p2 = Temp.CreateFile().WriteAllBytes(ProprietaryTestResources.NetFX.v2_0_50727.System).Path;
            var text = @"namespace N {}";

            var comp = CSharpCompilation.Create(
                "DupSignedRefs",
                new[] { SyntaxFactory.ParseSyntaxTree(text) },
                new[] { MetadataReference.CreateFromFile(p1), MetadataReference.CreateFromFile(p2) },
                TestOptions.ReleaseDll.WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default));

            comp.VerifyDiagnostics(
                // error CS1703: Multiple assemblies with equivalent identity have been imported: '...\v4.0.30319\System.dll' and '...\v2.0.50727\System.dll'. Remove one of the duplicate references.
                Diagnostic(ErrorCode.ERR_DuplicateImport).WithArguments(p1, p2));
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void CS1704ERR_DuplicateImportSimple()
        {
            var libSource = @"
using System;
public class A { }";


            var c1 = CreateCompilationWithMscorlib(libSource, options: TestOptions.ReleaseDll, assemblyName: "CS1704");

            var dir1 = Temp.CreateDirectory();
            var exe1 = dir1.CreateFile("CS1704.dll");
            var pdb1 = dir1.CreateFile("CS1704.pdb");

            var dir2 = Temp.CreateDirectory();
            var exe2 = dir2.CreateFile("CS1704.dll");
            var pdb2 = dir2.CreateFile("CS1704.pdb");

            using (var output = exe1.Open())
            {
                using (var outputPdb = pdb1.Open())
                {
                    c1.Emit(output, outputPdb);
                }
            }

            using (var output = exe2.Open())
            {
                using (var outputPdb = pdb2.Open())
                {
                    c1.Emit(output, outputPdb);
                }
            }

            var ref1 = AssemblyMetadata.CreateFromFile(exe1.Path).GetReference(aliases: ImmutableArray.Create("A1"));
            var ref2 = AssemblyMetadata.CreateFromFile(exe2.Path).GetReference(aliases: ImmutableArray.Create("A2"));

            var source = @"
extern alias A1;
extern alias A2;

class B : A1::A { }
class C : A2::A { }
";
            // Dev12 reports CS1704. An assembly with the same simple name '...' has already been imported. 
            // We consider the second reference a duplicate and ignore it (merging the aliases).

            CreateCompilationWithMscorlib(source, new[] { ref1, ref2 }).VerifyDiagnostics();
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void WeakIdentitiesWithDifferentVersions()
        {
            var sourceLibV1 = @"
using System.Reflection;
[assembly: AssemblyVersion(""1.0.0.0"")]

public class C1 { }
";

            var sourceLibV2 = @"
using System.Reflection;
[assembly: AssemblyVersion(""2.0.0.0"")]

public class C2 { }
";
            var sourceRefLibV1 = @"
public class P 
{
    public C1 x;
}
";

            var sourceMain = @"
public class Q
{
    public P x;
    public C1 y;
    public C2 z;
}
";

            var libV1 = CreateCompilationWithMscorlib(sourceLibV1, assemblyName: "Lib");
            var libV2 = CreateCompilationWithMscorlib(sourceLibV2, assemblyName: "Lib");

            var refLibV1 = CreateCompilationWithMscorlib(sourceRefLibV1,
                new[] { new CSharpCompilationReference(libV1) },
                assemblyName: "RefLibV1");

            var main = CreateCompilationWithMscorlib(sourceMain,
                new[] { new CSharpCompilationReference(libV1), new CSharpCompilationReference(refLibV1), new CSharpCompilationReference(libV2) },
                assemblyName: "Main");

            main.VerifyDiagnostics(
                // error CS1704: An assembly with the same simple name 'Lib' has already been imported. Try removing one of the references (e.g. 'Lib') or sign them to enable side-by-side.
                Diagnostic(ErrorCode.ERR_DuplicateImportSimple).WithArguments("Lib", "Lib"),
                // (5,12): error CS0246: The type or namespace name 'C1' could not be found (are you missing a using directive or an assembly reference?)
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "C1").WithArguments("C1"));
        }

        /// <summary>
        /// Although the CLR considers all WinRT references equivalent the Dev11 C# and VB compilers still 
        /// compare their identities as if they were regular managed dlls.
        /// </summary>
        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void WinMd_SameSimpleNames_SameVersions()
        {
            var sourceMain = @"
public class Q
{
    public C1 y;
    public C2 z;
}
";
            // W1.dll: (W, Version=255.255.255.255, Culture=null, PKT=null) 
            // W2.dll: (W, Version=255.255.255.255, Culture=null, PKT=null) 

            using (AssemblyMetadata metadataLib1 = AssemblyMetadata.CreateFromImage(TestResources.WinRt.W1),
                                    metadataLib2 = AssemblyMetadata.CreateFromImage(TestResources.WinRt.W2))
            {
                var mdRefLib1 = metadataLib1.GetReference(filePath: @"C:\W1.dll");
                var mdRefLib2 = metadataLib2.GetReference(filePath: @"C:\W2.dll");

                var main = CreateCompilationWithMscorlib(sourceMain,
                    new[] { mdRefLib1, mdRefLib2 });

                // Dev12 reports CS1704. An assembly with the same simple name '...' has already been imported. 
                // We consider the second reference a duplicate and ignore it.

                main.VerifyDiagnostics(
                    // (4,12): error CS0246: The type or namespace name 'C1' could not be found (are you missing a using directive or an assembly reference?)
                    Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "C1").WithArguments("C1"));
            }
        }

        /// <summary>
        /// Although the CLR considers all WinRT references equivalent the Dev11 C# and VB compilers still 
        /// compare their identities as if they were regular managed dlls.
        /// </summary>
        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void WinMd_DifferentSimpleNames()
        {
            var sourceMain = @"
public class Q
{
    public C1 y;
    public CB z;
}
";
            // W1.dll: (W, Version=255.255.255.255, Culture=null, PKT=null) 
            // WB.dll: (WB, Version=255.255.255.255, Culture=null, PKT=null) 

            using (AssemblyMetadata metadataLib1 = AssemblyMetadata.CreateFromImage(TestResources.WinRt.W1),
                                    metadataLib2 = AssemblyMetadata.CreateFromImage(TestResources.WinRt.WB))
            {
                var mdRefLib1 = metadataLib1.GetReference(filePath: @"C:\W1.dll");
                var mdRefLib2 = metadataLib2.GetReference(filePath: @"C:\WB.dll");

                var main = CreateCompilationWithMscorlib(sourceMain,
                    new[] { mdRefLib1, mdRefLib2 });

                main.VerifyDiagnostics();
            }
        }

        /// <summary>
        /// Although the CLR considers all WinRT references equivalent the Dev11 C# and VB compilers still 
        /// compare their identities as if they were regular managed dlls.
        /// </summary>
        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void WinMd_SameSimpleNames_DifferentVersions()
        {
            var sourceMain = @"
public class Q
{
    public CB y;
    public CB_V1 z;
}
";
            // WB.dll:          (WB, Version=255.255.255.255, Culture=null, PKT=null) 
            // WB_Version1.dll: (WB, Version=1.0.0.0, Culture=null, PKT=null) 

            using (AssemblyMetadata metadataLib1 = AssemblyMetadata.CreateFromImage(TestResources.WinRt.WB),
                                    metadataLib2 = AssemblyMetadata.CreateFromImage(TestResources.WinRt.WB_Version1))
            {
                var mdRefLib1 = metadataLib1.GetReference(filePath: @"C:\WB.dll");
                var mdRefLib2 = metadataLib2.GetReference(filePath: @"C:\WB_Version1.dll");

                var main = CreateCompilationWithMscorlib(sourceMain,
                    new[] { mdRefLib1, mdRefLib2 });

                main.VerifyDiagnostics(
                    // error CS1704: An assembly with the same simple name 'WB' has already been imported. Try removing one of the references (e.g. 'C:\WB.dll') or sign them to enable side-by-side.
                    Diagnostic(ErrorCode.ERR_DuplicateImportSimple).WithArguments("WB", @"C:\WB.dll"),
                    // (4,12): error CS0246: The type or namespace name 'CB' could not be found (are you missing a using directive or an assembly reference?)
                    Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "CB").WithArguments("CB"));
            }
        }

        /// <summary>
        /// We replicate the Dev11 behavior here but is there any real world scenario for this?
        /// </summary>
        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void MetadataReferencesDifferInCultureOnly()
        {
            var arSA = TestReferences.SymbolsTests.Versioning.AR_SA;
            var enUS = TestReferences.SymbolsTests.Versioning.EN_US;

            var source = @"
public class A 
{
   public arSA a = new arSA();
   public enUS b = new enUS();
}
";

            var compilation = CreateCompilationWithMscorlib(source, references: new[] { arSA, enUS });
            var arSA_sym = compilation.GetReferencedAssemblySymbol(arSA);
            var enUS_sym = compilation.GetReferencedAssemblySymbol(enUS);

            Assert.Equal("ar-SA", arSA_sym.Identity.CultureName);
            Assert.Equal("en-US", enUS_sym.Identity.CultureName);

            compilation.VerifyDiagnostics();
        }

        private class ReferenceResolver1 : TestMetadataReferenceResolver
        {
            public readonly string path1, path2;

            public ReferenceResolver1(string path1, string path2)
            {
                this.path1 = path1;
                this.path2 = path2;
            }

            public override string ResolveReference(string reference, string baseFilePath)
            {
                switch (reference)
                {
                    case "1":
                        resolved1 = true;
                        return path1;

                    case "2.dll":
                        resolved2 = true;
                        return path2;

                    default:
                        return base.ResolveReference(reference, baseFilePath);
                }
            }

            public bool resolved1, resolved2;
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void ReferenceResolution1()
        {
            var path1 = Temp.CreateFile().WriteAllBytes(TestResources.SymbolsTests.General.MDTestLib1).Path;
            var path2 = Temp.CreateFile().WriteAllBytes(TestResources.SymbolsTests.General.MDTestLib2).Path;

            var resolver = new ReferenceResolver1(path1, path2);
            var c1 = CSharpCompilation.Create("c1",
                syntaxTrees: new[]
                {
                    Parse("#r \"1\"", options: TestOptions.Script),
                    Parse("#r \"2.dll\"", options: TestOptions.Script),
                },
                options: TestOptions.ReleaseDll
                    .WithMetadataReferenceResolver(new AssemblyReferenceResolver(resolver, MetadataFileReferenceProvider.Default)));

            Assert.NotNull(c1.Assembly); // force creation of SourceAssemblySymbol

            var dirRefs = c1.DirectiveReferences;
            var assemblySymbol1 = c1.GetReferencedAssemblySymbol(dirRefs[0]);
            var assemblySymbol2 = c1.GetReferencedAssemblySymbol(dirRefs[1]);

            Assert.Equal("MDTestLib1", assemblySymbol1.Name);
            Assert.Equal("MDTestLib2", assemblySymbol2.Name);

            Assert.True(resolver.resolved1);
            Assert.True(resolver.resolved2);
        }

        private class TestException : Exception
        {
        }

        private class ErroneousReferenceResolver : TestMetadataReferenceResolver
        {
            public ErroneousReferenceResolver()
            {
            }

            public override string ResolveReference(string reference, string baseFilePath)
            {
                switch (reference)
                {
                    case "throw": throw new TestException();
                }

                return base.ResolveReference(reference, baseFilePath);
            }
        }

        private class ErroneousMetadataReferenceProvider : MetadataFileReferenceProvider
        {
            public override PortableExecutableReference GetReference(string fullPath, MetadataReferenceProperties properties = default(MetadataReferenceProperties))
            {
                switch (fullPath)
                {
                    case @"c:\throw.dll": throw new TestException();
                }

                return null;
            }
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void ReferenceResolution_ExceptionsFromResolver()
        {
            var options = TestOptions.ReleaseDll.
                WithMetadataReferenceResolver(new AssemblyReferenceResolver(new ErroneousReferenceResolver(), MetadataFileReferenceProvider.Default));

            foreach (var tree in new[]
            {
                Parse("#r \"throw\"", options: TestOptions.Script),
            })
            {
                var c = CSharpCompilation.Create("c", syntaxTrees: new[] { tree }, options: options);
                Assert.Throws<TestException>(() => { var a = c.Assembly; });
            }
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void ReferenceResolution_ExceptionsFromProvider()
        {
            var provider = new ErroneousMetadataReferenceProvider();

            var c1 = CSharpCompilation.Create("c",
                syntaxTrees: new[] { Parse(@"#r ""c:\throw.dll""", options: TestOptions.Script) },
                options: TestOptions.ReleaseDll.
                    WithMetadataReferenceResolver(new AssemblyReferenceResolver(new MappingReferenceResolver(files: new Dictionary<string, string>() { { @"c:\throw.dll", @"c:\throw.dll" } }), provider)));


            Assert.Throws<TestException>(() => { var a = c1.Assembly; });

            var c2 = CSharpCompilation.Create("c",
                references: new[] { MscorlibRef },
                syntaxTrees: new[] { Parse(@"#r ""c:\null.dll""", options: TestOptions.Script) },
                options: TestOptions.ReleaseDll.
                    WithMetadataReferenceResolver(new AssemblyReferenceResolver(new MappingReferenceResolver(files: new Dictionary<string, string>() { { @"c:\null.dll", @"c:\null.dll" } }), provider)));

            c2.VerifyDiagnostics(
                // (1,1): error CS0006: Metadata file 'c:\null.dll' could not be found
                Diagnostic(ErrorCode.ERR_NoMetadataFile, @"#r ""c:\null.dll""").WithArguments(@"c:\null.dll"));
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void ResolvedReferencesCaching()
        {
            var c1 = CSharpCompilation.Create("foo",
                syntaxTrees: new[] { Parse("class C {}") },
                references: new[] { MscorlibRef, SystemCoreRef, SystemRef });

            var a1 = c1.SourceAssembly;

            var c2 = c1.AddSyntaxTrees(Parse("class D { }"));

            var a2 = c2.SourceAssembly;
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void CyclesInReferences()
        {
            var sourceA = @"
public class A { }
";

            var a = CreateCompilationWithMscorlib(sourceA, assemblyName: "A");

            var sourceB = @"
public class B : A { } 
public class Foo {}
";
            var b = CreateCompilationWithMscorlib(sourceB, new[] { new CSharpCompilationReference(a) }, assemblyName: "B");
            var refB = MetadataReference.CreateFromImage(b.EmitToArray());

            var sourceA2 = @"
public class A 
{ 
    public Foo x = new Foo(); 
}
";
            // construct A2 that has a reference to assembly identity "B".
            var a2 = CreateCompilationWithMscorlib(sourceA2, new[] { refB }, assemblyName: "A");
            var refA2 = MetadataReference.CreateFromImage(a2.EmitToArray());
            var symbolB = a2.GetReferencedAssemblySymbol(refB);
            Assert.True(symbolB is Microsoft.CodeAnalysis.CSharp.Symbols.Metadata.PE.PEAssemblySymbol, "PE symbol expected");

            // force A assembly symbol to be added to a metadata cache:
            var c = CreateCompilationWithMscorlib("class C : A {}", new[] { refA2, refB }, assemblyName: "C");
            var symbolA2 = c.GetReferencedAssemblySymbol(refA2);
            Assert.True(symbolA2 is Microsoft.CodeAnalysis.CSharp.Symbols.Metadata.PE.PEAssemblySymbol, "PE symbol expected");
            Assert.Equal(1, ((AssemblyMetadata)refA2.GetMetadata()).CachedSymbols.WeakCount);

            GC.KeepAlive(symbolA2);

            // Recompile "B" and remove int Foo. The assembly manager should not reuse symbols for A since they are referring to old version of B.
            var b2 = CreateCompilationWithMscorlib(@"
public class B : A 
{ 
    public void Bar()
    {
        object objX = this.x;
    }
}
", new[] { refA2 }, assemblyName: "B");

            // TODO (tomat): Dev11 also reports:
            // b2.cs(5,28): error CS0570: 'A.x' is not supported by the language

            b2.VerifyDiagnostics(
                // (6,28): error CS7068: Reference to type 'Foo' claims it is defined in this assembly, but it is not defined in source or any added modules
                //         object objX = this.x;
                Diagnostic(ErrorCode.ERR_MissingTypeInSource, "x").WithArguments("Foo"));
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void BoundReferenceCaching_CyclesInReferences()
        {
            var a = CreateCompilationWithMscorlib("public class A { }", assemblyName: "A");
            var b = CreateCompilationWithMscorlib("public class B : A { } ", new[] { new CSharpCompilationReference(a) }, assemblyName: "B");
            var refB = MetadataReference.CreateFromImage(b.EmitToArray());

            // construct A2 that has a reference to assembly identity "B".
            var a2 = CreateCompilationWithMscorlib(@"public class A { B B; }", new[] { refB }, assemblyName: "A");
            var refA2 = MetadataReference.CreateFromImage(a2.EmitToArray());


            var withCircularReference1 = CreateCompilationWithMscorlib(@"public class B : A { }", new[] { refA2 }, assemblyName: "B");
            var withCircularReference2 = withCircularReference1.WithOptions(TestOptions.ReleaseDll);
            Assert.NotSame(withCircularReference1, withCircularReference2);

            // until we try to reuse bound references we share the manager:
            Assert.True(withCircularReference1.ReferenceManagerEquals(withCircularReference2));

            var assembly1 = withCircularReference1.SourceAssembly;
            Assert.True(withCircularReference1.ReferenceManagerEquals(withCircularReference2));

            var assembly2 = withCircularReference2.SourceAssembly;
            Assert.False(withCircularReference1.ReferenceManagerEquals(withCircularReference2));

            var refA2_symbol1 = withCircularReference1.GetReferencedAssemblySymbol(refA2);
            var refA2_symbol2 = withCircularReference2.GetReferencedAssemblySymbol(refA2);
            Assert.NotNull(refA2_symbol1);
            Assert.NotNull(refA2_symbol2);
            Assert.NotSame(refA2_symbol1, refA2_symbol2);
        }

        [WorkItem(546828, "DevDiv")]
        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void MetadataDependsOnSource()
        {
            // {0} is the body of the ReachFramework assembly reference.
            var ilTemplate = @"
.assembly extern ReachFramework
{{
{0}
}}
.assembly extern mscorlib
{{
  .publickeytoken = (B7 7A 5C 56 19 34 E0 89 )                         // .z\V.4..
  .ver 4:0:0:0
}}
.assembly PresentationFramework
{{
  .publickey = (00 24 00 00 04 80 00 00 94 00 00 00 06 02 00 00   // .$..............
                00 24 00 00 52 53 41 31 00 04 00 00 01 00 01 00   // .$..RSA1........
                B5 FC 90 E7 02 7F 67 87 1E 77 3A 8F DE 89 38 C8   // ......g..w:...8.
                1D D4 02 BA 65 B9 20 1D 60 59 3E 96 C4 92 65 1E   // ....e. .`Y>...e.
                88 9C C1 3F 14 15 EB B5 3F AC 11 31 AE 0B D3 33   // ...?....?..1...3
                C5 EE 60 21 67 2D 97 18 EA 31 A8 AE BD 0D A0 07   // ..`!g-...1......
                2F 25 D8 7D BA 6F C9 0F FD 59 8E D4 DA 35 E4 4C   // /%.}}.o...Y...5.L
                39 8C 45 43 07 E8 E3 3B 84 26 14 3D AE C9 F5 96   // 9.EC...;.&.=....
                83 6F 97 C8 F7 47 50 E5 97 5C 64 E2 18 9F 45 DE   // .o...GP..\d...E.
                F4 6B 2A 2B 12 47 AD C3 65 2B F5 C3 08 05 5D A9 ) // .k*+.G..e+....].
  .ver 4:0:0:0
}}

.module PresentationFramework.dll
// MVID: {{CBA9159C-5BB4-49BC-B41D-AF055BF1C0AB}}
.imagebase 0x00400000
.file alignment 0x00000200
.stackreserve 0x00100000
.subsystem 0x0003       // WINDOWS_CUI
.corflags 0x00000001    //  ILONLY
// Image base: 0x04D00000


// =============== CLASS MEMBERS DECLARATION ===================

.class public auto ansi System.Windows.Controls.PrintDialog
       extends [mscorlib]System.Object
{{
  .method public hidebysig instance class [ReachFramework]System.Printing.PrintTicket 
          Test() cil managed
  {{
    ret
  }}

  .method public hidebysig specialname rtspecialname 
          instance void  .ctor() cil managed
  {{
    ret
  }}
}}
";

            var csharp = @"
using System.Windows.Controls;

namespace System.Printing
{
    public class PrintTicket
    {
    }
}

class Test
{
    static void Main()
    {
        var dialog = new PrintDialog();
        var p = dialog.Test();
    }
}
";
            // ref only specifies name
            {
                var il = string.Format(ilTemplate, "");
                var ilRef = CompileIL(il, appendDefaultHeader: false);
                var comp = CreateCompilationWithMscorlib(csharp, new[] { ilRef }, assemblyName: "ReachFramework");
                comp.VerifyDiagnostics();
            }

            // public key specified by ref, but not def
            {
                var il = string.Format(ilTemplate, "  .publickeytoken = (31 BF 38 56 AD 36 4E 35 )                         // 1.8V.6N5");
                var ilRef = CompileIL(il, appendDefaultHeader: false);
                CreateCompilationWithMscorlib(csharp, new[] { ilRef }, assemblyName: "ReachFramework").VerifyDiagnostics();
            }

            // version specified by ref, but not def
            {
                var il = string.Format(ilTemplate, "  .ver 4:0:0:0");
                var ilRef = CompileIL(il, appendDefaultHeader: false);
                CreateCompilationWithMscorlib(csharp, new[] { ilRef }, assemblyName: "ReachFramework").VerifyDiagnostics();
            }

            // culture specified by ref, but not def
            {
                var il = string.Format(ilTemplate, "  .locale = (65 00 6E 00 2D 00 63 00 61 00 00 00 )             // e.n.-.c.a...");
                var ilRef = CompileIL(il, appendDefaultHeader: false);
                CreateCompilationWithMscorlib(csharp, new[] { ilRef }, assemblyName: "ReachFramework").VerifyDiagnostics();
            }
        }

        [WorkItem(546828, "DevDiv")]
        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void MetadataDependsOnMetadataOrSource()
        {
            var il = @"
.assembly extern ReachFramework
{
  .ver 4:0:0:0
}
.assembly extern mscorlib
{
  .publickeytoken = (B7 7A 5C 56 19 34 E0 89 )                         // .z\V.4..
  .ver 4:0:0:0
}
.assembly PresentationFramework
{
  .publickey = (00 24 00 00 04 80 00 00 94 00 00 00 06 02 00 00   // .$..............
                00 24 00 00 52 53 41 31 00 04 00 00 01 00 01 00   // .$..RSA1........
                B5 FC 90 E7 02 7F 67 87 1E 77 3A 8F DE 89 38 C8   // ......g..w:...8.
                1D D4 02 BA 65 B9 20 1D 60 59 3E 96 C4 92 65 1E   // ....e. .`Y>...e.
                88 9C C1 3F 14 15 EB B5 3F AC 11 31 AE 0B D3 33   // ...?....?..1...3
                C5 EE 60 21 67 2D 97 18 EA 31 A8 AE BD 0D A0 07   // ..`!g-...1......
                2F 25 D8 7D BA 6F C9 0F FD 59 8E D4 DA 35 E4 4C   // /%.}.o...Y...5.L
                39 8C 45 43 07 E8 E3 3B 84 26 14 3D AE C9 F5 96   // 9.EC...;.&.=....
                83 6F 97 C8 F7 47 50 E5 97 5C 64 E2 18 9F 45 DE   // .o...GP..\d...E.
                F4 6B 2A 2B 12 47 AD C3 65 2B F5 C3 08 05 5D A9 ) // .k*+.G..e+....].
  .ver 4:0:0:0
}

.module PresentationFramework.dll
// MVID: {CBA9159C-5BB4-49BC-B41D-AF055BF1C0AB}
.imagebase 0x00400000
.file alignment 0x00000200
.stackreserve 0x00100000
.subsystem 0x0003       // WINDOWS_CUI
.corflags 0x00000001    //  ILONLY
// Image base: 0x04D00000


// =============== CLASS MEMBERS DECLARATION ===================

.class public auto ansi System.Windows.Controls.PrintDialog
       extends [mscorlib]System.Object
{
  .method public hidebysig instance class [ReachFramework]System.Printing.PrintTicket 
          Test() cil managed
  {
    ret
  }

  .method public hidebysig specialname rtspecialname 
          instance void  .ctor() cil managed
  {
    ret
  }
}
";

            var csharp = @"
namespace System.Printing
{
    public class PrintTicket
    {
    }
}
";
            var oldVersion = @"[assembly: System.Reflection.AssemblyVersion(""1.0.0.0"")]";
            var newVersion = @"[assembly: System.Reflection.AssemblyVersion(""4.0.0.0"")]";

            var ilRef = CompileIL(il, appendDefaultHeader: false);
            var oldMetadata = AssemblyMetadata.CreateFromImage(CreateCompilationWithMscorlib(oldVersion + csharp, assemblyName: "ReachFramework").EmitToArray());
            var oldRef = oldMetadata.GetReference();

            var comp = CreateCompilationWithMscorlib(newVersion + csharp, new[] { ilRef, oldRef }, assemblyName: "ReachFramework");
            comp.VerifyDiagnostics();

            var method = comp.GlobalNamespace.
                GetMember<NamespaceSymbol>("System").
                GetMember<NamespaceSymbol>("Windows").
                GetMember<NamespaceSymbol>("Controls").
                GetMember<NamedTypeSymbol>("PrintDialog").
                GetMember<MethodSymbol>("Test");

            AssemblyIdentity actualIdentity = method.ReturnType.ContainingAssembly.Identity;

            // Even though the compilation has the correct version number, the referenced binary is preferred.
            Assert.Equal(oldMetadata.GetAssembly().Identity, actualIdentity);
            Assert.NotEqual(comp.Assembly.Identity, actualIdentity);
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        [WorkItem(546900, "DevDiv")]
        public void MetadataRefersToSourceAssemblyModule()
        {
            var srcA = @"
.assembly extern b
{
  .ver 0:0:0:0
}
.assembly extern mscorlib
{
  .publickeytoken = (B7 7A 5C 56 19 34 E0 89 )
  .ver 4:0:0:0
}
.assembly a
{
  .hash algorithm 0x00008004
  .ver 0:0:0:0
}
.module a.dll

.class public auto ansi beforefieldinit A
       extends [b]B
{
  .method public hidebysig specialname rtspecialname 
          instance void  .ctor() cil managed
  {
    .maxstack  8
    IL_0000:  ldarg.0
    IL_0001:  call       instance void [b]B::.ctor()
    IL_0006:  ret
  }
}";
            var aRef = CompileIL(srcA, appendDefaultHeader: false);

            string srcB = @"
public class B
{
	public A A;
}";

            var b = CreateCompilationWithMscorlib(srcB, references: new[] { aRef }, options: TestOptions.ReleaseModule.WithModuleName("mod.netmodule"), assemblyName: "B");
            b.VerifyDiagnostics();
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        [WorkItem(530839, "DevDiv")]
        public void EmbedInteropTypesReferences()
        {
            var libSource = @"
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyVersion(""1.0.0.0"")]
[assembly: Guid(""49a1950e-3e35-4595-8cb9-920c64c44d67"")]
[assembly: PrimaryInteropAssembly(1, 0)]
[assembly: ImportedFromTypeLib(""Lib"")]

[ComImport()]
[Guid(""49a1950e-3e35-4595-8cb9-920c64c44d68"")]
public interface I { }
";

            var mainSource = @"
public class C : I { } 
";

            var lib = CreateCompilationWithMscorlib(libSource, assemblyName: "lib");
            var refLib = ((MetadataImageReference)lib.EmitToImageReference()).WithEmbedInteropTypes(true);
            var main = CreateCompilationWithMscorlib(mainSource, new[] { refLib }, assemblyName: "main");

            CompileAndVerify(main, validator: (pe, _) =>
            {
                var reader = pe.GetMetadataReader();

                var assemblyRef = reader.AssemblyReferences.AsEnumerable().Single();
                var name = reader.GetString(reader.GetAssemblyReference(assemblyRef).Name);
                Assert.Equal(name, "mscorlib");
            },
            verify: false);
        }

        [WorkItem(531537, "DevDiv")]
        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void ModuleSymbolReuse()
        {
            var text1 = @"
class C
{
    TypeFromModule M() { }
}
";

            // Doesn't really matter what this text is - just need a delta.
            var text2 = @"
class D
{
}
";

            var assemblyMetadata = AssemblyMetadata.CreateFromImage(CreateCompilationWithMscorlib("public class TypeDependedOnByModule { }", assemblyName: "lib1").EmitToArray());
            var assemblyRef = assemblyMetadata.GetReference();
            var moduleRef = CreateCompilationWithMscorlib("public class TypeFromModule : TypeDependedOnByModule { }", new[] { assemblyRef }, options: TestOptions.ReleaseModule, assemblyName: "lib2").EmitToImageReference();

            var comp1 = CreateCompilationWithMscorlib(text1, new MetadataReference[]
            {
                moduleRef,
                assemblyRef,
            });
            var tree1 = comp1.SyntaxTrees.Single();

            var moduleSymbol1 = comp1.GetReferencedModuleSymbol(moduleRef);
            Assert.Equal(comp1.Assembly, moduleSymbol1.ContainingAssembly);

            var moduleReferences1 = moduleSymbol1.GetReferencedAssemblies();
            Assert.Contains(assemblyMetadata.GetAssembly().Identity, moduleReferences1);

            var moduleTypeSymbol1 = comp1.GlobalNamespace.GetMember<NamedTypeSymbol>("TypeFromModule");
            Assert.Equal(moduleSymbol1, moduleTypeSymbol1.ContainingModule);
            Assert.Equal(comp1.Assembly, moduleTypeSymbol1.ContainingAssembly);

            var tree2 = tree1.WithInsertAt(text1.Length, text2);
            var comp2 = comp1.ReplaceSyntaxTree(tree1, tree2);

            var moduleSymbol2 = comp2.GetReferencedModuleSymbol(moduleRef);
            Assert.Equal(comp2.Assembly, moduleSymbol2.ContainingAssembly);

            var moduleReferences2 = moduleSymbol2.GetReferencedAssemblies();

            var moduleTypeSymbol2 = comp2.GlobalNamespace.GetMember<NamedTypeSymbol>("TypeFromModule");
            Assert.Equal(moduleSymbol2, moduleTypeSymbol2.ContainingModule);
            Assert.Equal(comp2.Assembly, moduleTypeSymbol2.ContainingAssembly);

            Assert.NotEqual(moduleSymbol1, moduleSymbol2);
            Assert.NotEqual(moduleTypeSymbol1, moduleTypeSymbol2);
            AssertEx.Equal(moduleReferences1, moduleReferences2);
        }

        [WorkItem(531537, "DevDiv")]
        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void ModuleSymbolReuse_ImplicitType()
        {
            var text1 = @"
namespace A
{
    void M() { }
";

            var text2 = @"
}
";

            // Note: we just need *a* module reference for the repro - we're not depending on its contents, name, etc.
            var moduleRef = CreateCompilationWithMscorlib("public class C { }", options: TestOptions.ReleaseModule, assemblyName: "lib").EmitToImageReference();

            var comp1 = CreateCompilationWithMscorlib(text1, new MetadataReference[]
            {
                moduleRef,
            });
            var tree1 = comp1.SyntaxTrees.Single();

            var implicitTypeCount1 = comp1.GlobalNamespace.GetMember<NamespaceSymbol>("A").GetMembers(TypeSymbol.ImplicitTypeName).Length;
            Assert.Equal(1, implicitTypeCount1);


            var tree2 = tree1.WithInsertAt(text1.Length, text2);
            var comp2 = comp1.ReplaceSyntaxTree(tree1, tree2);

            var implicitTypeCount2 = comp2.GlobalNamespace.GetMember<NamespaceSymbol>("A").GetMembers(TypeSymbol.ImplicitTypeName).Length;
            Assert.Equal(1, implicitTypeCount2);
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void CachingAndVisibility()
        {
            var cPublic = CreateCompilationWithMscorlib("class C { }", options: TestOptions.ReleaseDll.WithMetadataImportOptions(MetadataImportOptions.Public));
            var cInternal = CreateCompilationWithMscorlib("class D { }", options: TestOptions.ReleaseDll.WithMetadataImportOptions(MetadataImportOptions.Internal));
            var cAll = CreateCompilationWithMscorlib("class E { }", options: TestOptions.ReleaseDll.WithMetadataImportOptions(MetadataImportOptions.All));

            var cPublic2 = CreateCompilationWithMscorlib("class C { }", options: TestOptions.ReleaseDll.WithMetadataImportOptions(MetadataImportOptions.Public));
            var cInternal2 = CreateCompilationWithMscorlib("class D { }", options: TestOptions.ReleaseDll.WithMetadataImportOptions(MetadataImportOptions.Internal));
            var cAll2 = CreateCompilationWithMscorlib("class E { }", options: TestOptions.ReleaseDll.WithMetadataImportOptions(MetadataImportOptions.All));

            Assert.NotSame(cPublic.Assembly.CorLibrary, cInternal.Assembly.CorLibrary);
            Assert.NotSame(cAll.Assembly.CorLibrary, cInternal.Assembly.CorLibrary);
            Assert.NotSame(cAll.Assembly.CorLibrary, cPublic.Assembly.CorLibrary);

            Assert.Same(cPublic.Assembly.CorLibrary, cPublic2.Assembly.CorLibrary);
            Assert.Same(cInternal.Assembly.CorLibrary, cInternal2.Assembly.CorLibrary);
            Assert.Same(cAll.Assembly.CorLibrary, cAll2.Assembly.CorLibrary);
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void ImportingPrivateNetModuleMembers()
        {
            string moduleSource = @"
internal class C
{
    private void m() { }
}
";
            string mainSource = @"
";
            var module = CreateCompilationWithMscorlib(moduleSource, options: TestOptions.ReleaseModule);
            var moduleRef = module.EmitToImageReference();

            // All
            var mainAll = CreateCompilationWithMscorlib(mainSource, new[] { moduleRef }, options: TestOptions.ReleaseDll.WithMetadataImportOptions(MetadataImportOptions.All));
            var mAll = mainAll.GlobalNamespace.GetMember<NamedTypeSymbol>("C").GetMembers("m");
            Assert.Equal(1, mAll.Length);

            // Internal
            var mainInternal = CreateCompilationWithMscorlib(mainSource, new[] { moduleRef }, options: TestOptions.ReleaseDll.WithMetadataImportOptions(MetadataImportOptions.Internal));
            var mInternal = mainInternal.GlobalNamespace.GetMember<NamedTypeSymbol>("C").GetMembers("m");
            Assert.Equal(0, mInternal.Length);

            // Public
            var mainPublic = CreateCompilationWithMscorlib(mainSource, new[] { moduleRef }, options: TestOptions.ReleaseDll.WithMetadataImportOptions(MetadataImportOptions.Public));
            var mPublic = mainPublic.GlobalNamespace.GetMember<NamedTypeSymbol>("C").GetMembers("m");
            Assert.Equal(0, mPublic.Length);
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        [WorkItem(531342, "DevDiv"), WorkItem(727122, "DevDiv")]
        public void PortableLibrary()
        {
            var plSource = @"public class C {}";
            var pl = CreateCompilation(plSource, new[] { MscorlibPP7Ref, SystemRuntimePP7Ref });
            var r1 = new CSharpCompilationReference(pl);

            var mainSource = @"public class D : C { }";

            // w/o facades:
            var main = CreateCompilation(mainSource, new MetadataReference[] { r1, MscorlibFacadeRef }, options: TestOptions.ReleaseDll);
            main.VerifyDiagnostics(
                // (1,18): error CS0012: The type 'System.Object' is defined in an assembly that is not referenced. You must add a reference to assembly 'System.Runtime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a'.
                Diagnostic(ErrorCode.ERR_NoTypeDef, "C").WithArguments("System.Object", "System.Runtime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"));

            // facade specified:
            main = CreateCompilation(mainSource, new MetadataReference[] { r1, MscorlibFacadeRef, SystemRuntimeFacadeRef });
            main.VerifyDiagnostics();
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        [WorkItem(762729, "DevDiv")]
        public void OverloadResolutionUseSiteWarning()
        {
            var libBTemplate = @"
[assembly: System.Reflection.AssemblyVersion(""{0}.0.0.0"")]
public class B {{ }}
";

            var libBv1 = CreateCompilationWithMscorlib(string.Format(libBTemplate, "1"), assemblyName: "B", options: s_signedDll);
            var libBv2 = CreateCompilationWithMscorlib(string.Format(libBTemplate, "2"), assemblyName: "B", options: s_signedDll);

            var libASource = @"
[assembly: System.Reflection.AssemblyVersion(""1.0.0.0"")]

public class A
{
    public void M(B b) { }
}
";

            var libAv1 = CreateCompilationWithMscorlib(
                libASource,
                new[] { new CSharpCompilationReference(libBv1) },
                assemblyName: "A",
                options: s_signedDll);

            var source = @"
public class Source
{
    public void Test()
    {
        A a = new A();
        a.M(null);
    }
}
";

            var comp = CreateCompilationWithMscorlib(source, new[] { new CSharpCompilationReference(libAv1), new CSharpCompilationReference(libBv2) });
            comp.VerifyDiagnostics(
                // (7,9): warning CS1701: Assuming assembly reference 'B, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'A' matches identity 'B, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'B', you may need to supply runtime policy
                //         a.M(null);
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin, "a.M").WithArguments("B, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "A", "B, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "B"));
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        [WorkItem(762729, "DevDiv")]
        public void MethodGroupConversionUseSiteWarning()
        {
            var libBTemplate = @"
[assembly: System.Reflection.AssemblyVersion(""{0}.0.0.0"")]
public class B {{ }}
";

            var libBv1 = CreateCompilationWithMscorlib(string.Format(libBTemplate, "1"), assemblyName: "B", options: s_signedDll);
            var libBv2 = CreateCompilationWithMscorlib(string.Format(libBTemplate, "2"), assemblyName: "B", options: s_signedDll);

            var libASource = @"
[assembly: System.Reflection.AssemblyVersion(""1.0.0.0"")]

public class A
{
    public void M(B b) { }
}
";

            var libAv1 = CreateCompilationWithMscorlib(
                libASource,
                new[] { new CSharpCompilationReference(libBv1) },
                assemblyName: "A",
                options: s_signedDll);

            var source = @"
public class Source
{
    public void Test()
    {
        A a = new A();
        System.Action<B> f = a.M;
    }
}
";

            var comp = CreateCompilationWithMscorlib(source, new[] { new CSharpCompilationReference(libAv1), new CSharpCompilationReference(libBv2) });
            comp.VerifyDiagnostics(
                // (7,30): warning CS1701: Assuming assembly reference 'B, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'A' matches identity 'B, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'B', you may need to supply runtime policy
                //         System.Action<B> f = a.M;
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin, "a.M").WithArguments("B, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "A", "B, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "B"));
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        [WorkItem(762729, "DevDiv")]
        public void IndexerUseSiteWarning()
        {
            var libBTemplate = @"
[assembly: System.Reflection.AssemblyVersion(""{0}.0.0.0"")]
public class B {{ }}
";

            var libBv1 = CreateCompilationWithMscorlib(string.Format(libBTemplate, "1"), assemblyName: "B", options: s_signedDll);
            var libBv2 = CreateCompilationWithMscorlib(string.Format(libBTemplate, "2"), assemblyName: "B", options: s_signedDll);

            var libASource = @"
[assembly: System.Reflection.AssemblyVersion(""1.0.0.0"")]

public class A
{
    public int this[B b] { get { return 0; } }
}
";

            var libAv1 = CreateCompilationWithMscorlib(libASource, new[] { new CSharpCompilationReference(libBv1) }, assemblyName: "A", options: s_signedDll);

            var source = @"
public class Source
{
    public void Test()
    {
        A a = new A();
        int x = a[null];
    }
}
";

            var comp = CreateCompilationWithMscorlib(source, new[] { new CSharpCompilationReference(libAv1), new CSharpCompilationReference(libBv2) });
            comp.VerifyDiagnostics(
                // (7,17): warning CS1701: Assuming assembly reference 'B, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'A' matches identity 'B, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'B', you may need to supply runtime policy
                //         int x = a[null];
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin, "a[null]").WithArguments("B, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "A", "B, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "B"));
        }

        [ClrOnlyFact(ClrOnlyReason.Signing)]
        [WorkItem(762729, "DevDiv")]
        public void Repro762729()
        {
            var libBTemplate = @"
[assembly: System.Reflection.AssemblyVersion(""{0}.0.0.0"")]

// To be implemented in library A.
public interface IGeneric<T>
{{
    void M();
}}

// To be implemented by superclass of class implementing IGeneric<T>.
public interface I
{{
}}

public static class Extensions
{{
    // To be invoked from the test assembly.
    public static void Extension<T>(this IGeneric<T> i) 
    {{
        i.M(); 
    }}
}}
";

            var libBv1 = CreateCompilationWithMscorlibAndSystemCore(string.Format(libBTemplate, "1"), assemblyName: "B", options: s_signedDll);
            var libBv2 = CreateCompilationWithMscorlibAndSystemCore(string.Format(libBTemplate, "2"), assemblyName: "B", options: s_signedDll);

            libBv1.EmitToImageReference();
            libBv2.EmitToImageReference();

            var libASource = @"
[assembly: System.Reflection.AssemblyVersion(""1.0.0.0"")]

public class ABase : I
{
}

public class A : ABase, IGeneric<AItem>
{
    void IGeneric<AItem>.M() { }
}

// Type argument for IGeneric<T>.  In the current assembly so there are no versioning issues.
public class AItem
{
}
";

            var libAv1 = CreateCompilationWithMscorlib(libASource, new[] { new CSharpCompilationReference(libBv1) }, assemblyName: "A", options: s_signedDll);

            libAv1.EmitToImageReference();

            var source = @"
public class Source
{
    public void Test(A a)
    {
        a.Extension();
    }
}
";

            var comp = CreateCompilationWithMscorlib(source, new[] { new CSharpCompilationReference(libAv1), new CSharpCompilationReference(libBv2) });
            comp.VerifyEmitDiagnostics(
                // warning CS1701: Assuming assembly reference 'B, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'A' matches identity 'B, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'B', you may need to supply runtime policy
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin).WithArguments("B, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "A", "B, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "B"),
                // (6,11): warning CS1701: Assuming assembly reference 'B, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'A' matches identity 'B, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'B', you may need to supply runtime policy
                //         a.Extension();
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin, "Extension").WithArguments("B, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "A", "B, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "B"),
                // (6,9): warning CS1701: Assuming assembly reference 'B, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' used by 'A' matches identity 'B, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2' of 'B', you may need to supply runtime policy
                //         a.Extension();
                Diagnostic(ErrorCode.WRN_UnifyReferenceMajMin, "a.Extension").WithArguments("B, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "A", "B, Version=2.0.0.0, Culture=neutral, PublicKeyToken=ce65828c82a341f2", "B"));
        }

        [WorkItem(905495, "DevDiv")]
        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void ReferenceWithNoMetadataSection()
        {
            var c = CreateCompilationWithMscorlib("", new[] { new TestImageReference(TestResources.MetadataTests.Basic.NativeApp, "NativeApp.exe") });
            c.VerifyDiagnostics(
                // error CS0009: Metadata file 'NativeApp.exe' could not be opened -- PE image doesn't contain managed metadata.
                Diagnostic(ErrorCode.FTL_MetadataCantOpenFile).WithArguments(@"NativeApp.exe", CodeAnalysisResources.PEImageDoesntContainManagedMetadata));
        }

        [WorkItem(2988, "https://github.com/dotnet/roslyn/issues/2988")]
        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void EmptyReference1()
        {
            var source = "class C { public static void Main() { } }";

            var c = CreateCompilationWithMscorlib(source, new[] { AssemblyMetadata.CreateFromImage(new byte[0]).GetReference(display: "Empty.dll") });
            c.VerifyDiagnostics(
                Diagnostic(ErrorCode.FTL_MetadataCantOpenFile).WithArguments(@"Empty.dll", CodeAnalysisResources.PEImageDoesntContainManagedMetadata));
        }

        [WorkItem(2992, "https://github.com/dotnet/roslyn/issues/2992")]
        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void MetadataDisposed()
        {
            var md = AssemblyMetadata.CreateFromImage(TestResources.NetFX.Minimal.mincorlib);
            var compilation = CSharpCompilation.Create("test", references: new[] { md.GetReference() });

            // Use the Compilation once to force lazy initialization of the underlying MetadataReader
            compilation.GetTypeByMetadataName("System.Int32").GetMembers();

            md.Dispose();

            Assert.Throws<ObjectDisposedException>(() => compilation.GetTypeByMetadataName("System.Int64").GetMembers());
        }

        [WorkItem(43)]
        [ClrOnlyFact(ClrOnlyReason.Signing)]
        public void ReusingCorLibManager()
        {
            var corlib1 = CreateCompilation("");
            var assembly1 = corlib1.Assembly;

            var corlib2 = corlib1.Clone();
            var assembly2 = corlib2.Assembly;

            Assert.Same(assembly1.CorLibrary, assembly1);
            Assert.Same(assembly2.CorLibrary, assembly2);
            Assert.True(corlib1.ReferenceManagerEquals(corlib2));
        }
    }
}
