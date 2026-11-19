// 组件胶水（Props 采纳与比较）的生成逻辑：纯 C#，不依赖 Roslyn。
// 由 C# 源生成器（src/tools/AwAUI.Generators）链接使用；.awaui 标记编译器也将链接同一份文件，
// 在生成组件类时直接产出相同的胶水（design/12「Props 与实例的关系」第 3 条，design/14「生成器之间互不可见」）。
// 输出只使用 C# 7.3 语法，.NET Framework 项目不提升语言版本也能编译。
#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace AwAUI.CodeGen
{
    /// <summary>要生成的胶水成员。组件手写了其中某个方法时，生成器只补齐其余的（只手写 PropsEqual 时仍需要生成的 AdoptProps）。</summary>
    [Flags]
    internal enum GlueMembers
    {
        None = 0,
        PropsEqual = 1,
        AdoptProps = 2,

        /// <summary>只在存在 init Props 时输出。</summary>
        CanAdoptProps = 4,
        All = PropsEqual | AdoptProps | CanAdoptProps,
    }

    /// <summary>一个 Prop：组件上声明的公共可写实例属性。</summary>
    internal sealed class GlueProp : IEquatable<GlueProp>
    {
        public GlueProp(string name, string typeName, bool isInitOnly)
        {
            Name = name;
            TypeName = typeName;
            IsInitOnly = isInitOnly;
        }

        /// <summary>属性名（不含 <c>@</c> 转义）。</summary>
        public string Name { get; }

        /// <summary>可在生成代码中直接书写的类型名（建议 <c>global::</c> 全限定）。</summary>
        public string TypeName { get; }

        /// <summary>只有 <c>init</c> 访问器：实例上无法更新，变化时组件重新挂载。</summary>
        public bool IsInitOnly { get; }

        public bool Equals(GlueProp? other) =>
            other is not null && Name == other.Name && TypeName == other.TypeName && IsInitOnly == other.IsInitOnly;

        public override bool Equals(object? obj) => Equals(obj as GlueProp);

        public override int GetHashCode() => (Name.GetHashCode() * 31 + TypeName.GetHashCode()) * 31 + (IsInitOnly ? 1 : 0);
    }

    /// <summary>外层类型（嵌套组件需要逐层输出 partial 声明）。</summary>
    internal sealed class GlueContainingType : IEquatable<GlueContainingType>
    {
        public GlueContainingType(string keyword, string nameWithTypeParameters)
        {
            Keyword = keyword;
            NameWithTypeParameters = nameWithTypeParameters;
        }

        /// <summary><c>class</c>、<c>struct</c>、<c>interface</c>、<c>record</c>、<c>record struct</c>。</summary>
        public string Keyword { get; }

        /// <summary>如 <c>Outer&lt;T&gt;</c>。</summary>
        public string NameWithTypeParameters { get; }

        public bool Equals(GlueContainingType? other) =>
            other is not null && Keyword == other.Keyword && NameWithTypeParameters == other.NameWithTypeParameters;

        public override bool Equals(object? obj) => Equals(obj as GlueContainingType);

        public override int GetHashCode() => Keyword.GetHashCode() * 31 + NameWithTypeParameters.GetHashCode();
    }

    /// <summary>生成一个组件胶水所需的全部信息（与来源无关：Roslyn 符号或 .awaui 语法都能填出它）。</summary>
    internal sealed class ComponentGlueModel : IEquatable<ComponentGlueModel>
    {
        public ComponentGlueModel(
            string? @namespace,
            IReadOnlyList<GlueContainingType> containingTypes,
            string className,
            string? typeParameters,
            IReadOnlyList<GlueProp> props,
            bool baseHasGlue,
            GlueMembers members = GlueMembers.All)
        {
            Namespace = string.IsNullOrEmpty(@namespace) ? null : @namespace;
            ContainingTypes = containingTypes;
            ClassName = className;
            TypeParameters = string.IsNullOrEmpty(typeParameters) ? null : typeParameters;
            Props = props;
            BaseHasGlue = baseHasGlue;
            Members = members;
        }

        /// <summary>要输出的成员（排除手写的）。</summary>
        public GlueMembers Members { get; }

        /// <summary>命名空间；全局命名空间为 null。</summary>
        public string? Namespace { get; }

        /// <summary>外层类型，最外层在前。</summary>
        public IReadOnlyList<GlueContainingType> ContainingTypes { get; }

        public string ClassName { get; }

        /// <summary>类型参数列表文本（如 <c>&lt;T&gt;</c>）；非泛型为 null。</summary>
        public string? TypeParameters { get; }

        /// <summary>本类负责的 Props（含没有胶水的基类组件上声明的 Props）。</summary>
        public IReadOnlyList<GlueProp> Props { get; }

        /// <summary>基类组件自己有胶水（生成的或手写的），生成的方法先调用基类实现。</summary>
        public bool BaseHasGlue { get; }

        /// <summary>本类在生成代码中的类型名（含类型参数）。</summary>
        public string SelfType => ClassName + (TypeParameters ?? "");

        public bool Equals(ComponentGlueModel? other)
        {
            if (other is null) return false;
            if (Namespace != other.Namespace || ClassName != other.ClassName || TypeParameters != other.TypeParameters
                || BaseHasGlue != other.BaseHasGlue || Members != other.Members) return false;
            return SequenceEqual(ContainingTypes, other.ContainingTypes) && SequenceEqual(Props, other.Props);
        }

        public override bool Equals(object? obj) => Equals(obj as ComponentGlueModel);

        public override int GetHashCode()
        {
            int h = ClassName.GetHashCode();
            h = h * 31 + (Namespace?.GetHashCode() ?? 0);
            h = h * 31 + Props.Count;
            return h;
        }

        static bool SequenceEqual<T>(IReadOnlyList<T> a, IReadOnlyList<T> b) where T : IEquatable<T>
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!a[i].Equals(b[i])) return false;
            return true;
        }
    }

    /// <summary>
    /// 组件胶水的 C# 输出：
    /// <list type="bullet">
    /// <item><c>PropsEqual</c>：逐个 Prop 用 <c>EqualityComparer&lt;T&gt;.Default</c> 比较。委托因此按 <c>Delegate.Equals</c>（同一方法 + 同一目标）比较：
    /// 方法组与只捕获 <c>this</c> 的 lambda 跨重建保持相等，不引起子组件重建；捕获了局部变量的 lambda 每次都是新闭包，子组件会重建，
    /// 从而不会留下引用旧闭包的处理器。</item>
    /// <item><c>AdoptProps</c>：拷贝全部可写（非 init）Props。</item>
    /// <item><c>CanAdoptProps</c>（仅当存在 init Props）：init Props 有变化时返回 false，协调器重新挂载该组件。</item>
    /// </list>
    /// </summary>
    internal static class ComponentGlueEmitter
    {
        public const string ComponentTypeName = "global::AwAUI.UI.Component";
        const string Comparer = "global::System.Collections.Generic.EqualityComparer<";

        /// <summary>生成完整的编译单元（源生成器使用）。</summary>
        public static string EmitFile(ComponentGlueModel model)
        {
            var sb = new StringBuilder();
            sb.Append("// <auto-generated/>\n");
            sb.Append("#pragma warning disable\n");
            string indent = "";
            if (model.Namespace is not null)
            {
                sb.Append("namespace ").Append(model.Namespace).Append('\n').Append("{\n");
                indent = "    ";
            }
            foreach (var c in model.ContainingTypes)
            {
                sb.Append(indent).Append("partial ").Append(c.Keyword).Append(' ').Append(c.NameWithTypeParameters).Append('\n');
                sb.Append(indent).Append("{\n");
                indent += "    ";
            }
            sb.Append(indent).Append("partial class ").Append(model.SelfType).Append('\n');
            sb.Append(indent).Append("{\n");
            EmitMembers(model, sb, indent + "    ");
            sb.Append(indent).Append("}\n");
            for (int i = 0; i < model.ContainingTypes.Count; i++)
            {
                indent = indent.Substring(4);
                sb.Append(indent).Append("}\n");
            }
            if (model.Namespace is not null) sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>只输出成员（标记编译器把它们放进自己生成的类中）。</summary>
        public static void EmitMembers(ComponentGlueModel model, StringBuilder sb, string indent)
        {
            string self = model.SelfType;
            bool hasInit = false;
            bool hasSettable = false;
            foreach (var p in model.Props)
            {
                if (p.IsInitOnly) hasInit = true;
                else hasSettable = true;
            }

            string i2 = indent + "    ";
            bool first = true;

            // PropsEqual
            if ((model.Members & GlueMembers.PropsEqual) != 0)
            {
                first = false;
                Line(sb, indent, "/// <summary>Props 相等比较（组件胶水，自动生成）。</summary>");
                Line(sb, indent, "protected override bool PropsEqual(" + ComponentTypeName + " other)");
                Line(sb, indent, "{");
                Line(sb, i2, "var __o = other as " + self + ";");
                Line(sb, i2, "if ((object)__o == null) return false;");
                if (model.BaseHasGlue) Line(sb, i2, "if (!base.PropsEqual(other)) return false;");
                EmitComparisonReturn(sb, i2, model.Props, includeInitOnly: true, onlyInitOnly: false);
                Line(sb, indent, "}");
            }

            // AdoptProps
            if ((model.Members & GlueMembers.AdoptProps) != 0)
            {
                if (!first) sb.Append('\n');
                first = false;
                Line(sb, indent, "/// <summary>从 Props 载体拷贝 Props（组件胶水，自动生成）。</summary>");
                Line(sb, indent, "protected override void AdoptProps(" + ComponentTypeName + " other)");
                Line(sb, indent, "{");
                if (model.BaseHasGlue) Line(sb, i2, "base.AdoptProps(other);");
                if (hasSettable)
                {
                    Line(sb, i2, "var __o = (" + self + ")other;");
                    foreach (var p in model.Props)
                    {
                        if (p.IsInitOnly) continue;
                        string n = Escape(p.Name);
                        Line(sb, i2, "this." + n + " = __o." + n + ";");
                    }
                }
                Line(sb, indent, "}");
            }

            if (hasInit && (model.Members & GlueMembers.CanAdoptProps) != 0)
            {
                if (!first) sb.Append('\n');
                Line(sb, indent, "/// <summary>init Props 变化时不能沿用实例（组件胶水，自动生成）。</summary>");
                Line(sb, indent, "protected override bool CanAdoptProps(" + ComponentTypeName + " other)");
                Line(sb, indent, "{");
                Line(sb, i2, "var __o = other as " + self + ";");
                Line(sb, i2, "if ((object)__o == null) return false;");
                if (model.BaseHasGlue) Line(sb, i2, "if (!base.CanAdoptProps(other)) return false;");
                EmitComparisonReturn(sb, i2, model.Props, includeInitOnly: true, onlyInitOnly: true);
                Line(sb, indent, "}");
            }
        }

        static void EmitComparisonReturn(StringBuilder sb, string indent, IReadOnlyList<GlueProp> props, bool includeInitOnly, bool onlyInitOnly)
        {
            var terms = new List<string>();
            foreach (var p in props)
            {
                if (onlyInitOnly && !p.IsInitOnly) continue;
                if (!includeInitOnly && p.IsInitOnly) continue;
                string n = Escape(p.Name);
                terms.Add(Comparer + p.TypeName + ">.Default.Equals(this." + n + ", __o." + n + ")");
            }
            if (terms.Count == 0)
            {
                Line(sb, indent, "return true;");
                return;
            }
            sb.Append(indent).Append("return ").Append(terms[0]);
            for (int i = 1; i < terms.Count; i++) sb.Append('\n').Append(indent).Append("    && ").Append(terms[i]);
            sb.Append(";\n");
        }

        static void Line(StringBuilder sb, string indent, string text) => sb.Append(indent).Append(text).Append('\n');

        /// <summary>C# 关键字作为标识符时加 <c>@</c>。</summary>
        public static string Escape(string identifier) => Keywords.Contains(identifier) ? "@" + identifier : identifier;

        static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
            "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
            "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
            "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
            "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
            "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
            "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
        };
    }
}
