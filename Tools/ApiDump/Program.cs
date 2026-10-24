using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// 只读元数据表，不加载程序集、不执行代码。
// 用法: ApiDump <dll路径> [类型名关键字...]
internal sealed class SigProv : ISignatureTypeProvider<string, object>
{
    private readonly MetadataReader md;
    public SigProv(MetadataReader m) { md = m; }

    public string GetArrayType(string e, ArrayShape s) => e + "[]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a)
        => g + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object c, int i) => "!!" + i;
    public string GetGenericTypeParameter(object c, int i) => "!" + i;
    public string GetModifiedType(string m, string u, bool req) => u;
    public string GetPinnedType(string e) => e;
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte raw)
    {
        TypeDefinition td = r.GetTypeDefinition(h);
        string ns = r.GetString(td.Namespace);
        return (string.IsNullOrEmpty(ns) ? "" : ns + ".") + r.GetString(td.Name);
    }
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte raw)
    {
        TypeReference tr = r.GetTypeReference(h);
        string ns = r.GetString(tr.Namespace);
        return (string.IsNullOrEmpty(ns) ? "" : ns + ".") + r.GetString(tr.Name);
    }
    public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte raw)
        => "spec";
}

internal static class Program
{
    private static string AttrName(MetadataReader md, CustomAttribute ca)
    {
        try
        {
            if (ca.Constructor.Kind == HandleKind.MemberReference)
            {
                MemberReference mr = md.GetMemberReference((MemberReferenceHandle)ca.Constructor);
                if (mr.Parent.Kind == HandleKind.TypeReference)
                {
                    TypeReference tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                    string ns2 = md.GetString(tr.Namespace);
                    return (string.IsNullOrEmpty(ns2) ? "" : ns2 + ".") + md.GetString(tr.Name);
                }
            }
            else if (ca.Constructor.Kind == HandleKind.MethodDefinition)
            {
                MethodDefinition m = md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor);
                TypeDefinition t = md.GetTypeDefinition(m.GetDeclaringType());
                return md.GetString(t.Name);
            }
        }
        catch { }
        return "?";
    }

    /// <summary>字符串参数的特性：prolog(2) + SerString(压缩长度 + UTF8)</summary>
    private static string DecodeBlob(MetadataReader md, CustomAttribute ca)
    {
        try
        {
            BlobReader r = md.GetBlobReader(ca.Value);
            r.ReadUInt16(); // prolog
            string s = r.ReadSerializedString();
            return s ?? "";
        }
        catch { return ""; }
    }

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: ApiDump <dll> [关键字...]");
            return 1;
        }

        string path = args[0];
        string[] filters = new string[args.Length - 1];
        Array.Copy(args, 1, filters, 0, filters.Length);

        using FileStream fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        MetadataReader md = pe.GetMetadataReader();

        int printed = 0;
        foreach (TypeDefinitionHandle th in md.TypeDefinitions)
        {
            TypeDefinition td = md.GetTypeDefinition(th);
            string ns = md.GetString(td.Namespace);
            string name = md.GetString(td.Name);
            string full = string.IsNullOrEmpty(ns) ? name : ns + "." + name;

            if (filters.Length > 0)
            {
                bool hit = false;
                foreach (string f in filters)
                {
                    if (full.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                }
                if (!hit) continue;
            }

            Console.WriteLine("== " + full);
            foreach (FieldDefinitionHandle fh in td.GetFields())
            {
                Console.WriteLine("   field   " + md.GetString(md.GetFieldDefinition(fh).Name));
            }
            foreach (PropertyDefinitionHandle ph in td.GetProperties())
            {
                Console.WriteLine("   prop    " + md.GetString(md.GetPropertyDefinition(ph).Name));
            }
            foreach (MethodDefinitionHandle mh in td.GetMethods())
            {
                MethodDefinition m = md.GetMethodDefinition(mh);
                if (m.Name.IsNil) continue;
                Console.WriteLine("   method  " + md.GetString(m.Name));
            }
            Console.WriteLine();
            printed++;
        }

        // --sig 模式：打印方法签名 + 属性类型
        if (filters.Length >= 2 && filters[0] == "--sig")
        {
            var prov = new SigProv(md);
            foreach (TypeDefinitionHandle th in md.TypeDefinitions)
            {
                TypeDefinition td = md.GetTypeDefinition(th);
                string ns0 = md.GetString(td.Namespace);
                string nm0 = md.GetString(td.Name);
                string full0 = string.IsNullOrEmpty(ns0) ? nm0 : ns0 + "." + nm0;
                if (full0.IndexOf(filters[1], StringComparison.OrdinalIgnoreCase) < 0) continue;

                Console.WriteLine("== " + full0);
                foreach (MethodDefinitionHandle mh in td.GetMethods())
                {
                    MethodDefinition m = md.GetMethodDefinition(mh);
                    MethodSignature<string> sig = m.DecodeSignature(prov, null);
                    Console.WriteLine("   " + sig.ReturnType + " " + md.GetString(m.Name)
                        + "(" + string.Join(", ", sig.ParameterTypes) + ")");
                }
                foreach (PropertyDefinitionHandle ph in td.GetProperties())
                {
                    PropertyDefinition pd = md.GetPropertyDefinition(ph);
                    MethodSignature<string> sig = pd.DecodeSignature(prov, null);
                    Console.WriteLine("   [prop] " + sig.ReturnType + " " + md.GetString(pd.Name));
                }
            }
            return 0;
        }

        // --attrs 模式：打印某个类型的属性名 + 自定义特性的字符串参数
        if (filters.Length >= 2 && filters[0] == "--attrs")
        {
            foreach (TypeDefinitionHandle th in md.TypeDefinitions)
            {
                TypeDefinition td = md.GetTypeDefinition(th);
                string ns = md.GetString(td.Namespace);
                string nm = md.GetString(td.Name);
                string full = string.IsNullOrEmpty(ns) ? nm : ns + "." + nm;
                if (full.IndexOf(filters[1], StringComparison.OrdinalIgnoreCase) < 0) continue;

                Console.WriteLine("== " + full);
                foreach (PropertyDefinitionHandle ph in td.GetProperties())
                {
                    PropertyDefinition pd = md.GetPropertyDefinition(ph);
                    string pname = md.GetString(pd.Name);
                    var attrs = new List<string>();
                    foreach (CustomAttributeHandle ah in pd.GetCustomAttributes())
                    {
                        CustomAttribute ca = md.GetCustomAttribute(ah);
                        string aname = AttrName(md, ca);
                        attrs.Add(aname + "(" + DecodeBlob(md, ca) + ")");
                    }
                    Console.WriteLine("   " + pname + "  ->  " + string.Join(", ", attrs.ToArray()));
                }
            }
            return 0;
        }

        Console.WriteLine("(共 " + printed + " 个类型)");
        return 0;
    }
}
