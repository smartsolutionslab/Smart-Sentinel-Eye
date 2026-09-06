using System.Text.RegularExpressions;

namespace SmartSentinelEye.Architecture.Tests;

/// <summary>
/// Guards the translation invariant behind spec 082 (#2068, #2071): when a
/// domain event carries a fab, the integration event derived from it carries
/// that fab in its <c>EventMetadata</c>.
///
/// <para>
/// <b>Why a guard and not a value object.</b> <c>EventMetadata.Fab</c> is
/// <c>string?</c> because ADR-0102 requires the null case for genuinely
/// fab-neutral events, so the type system permits <c>null</c> at every site by
/// design. The obligation lives in the relation between two types, not in
/// either of them. Reflection cannot help either: a passed argument leaves no
/// trace in metadata. So this reads source, as
/// <see cref="HandlerDeconstructionTests"/> does.
/// </para>
///
/// <para>
/// <b>The rule is a derivation, not a register.</b> Both sides come from
/// source. The obligation side is the handler's own first-parameter record
/// header; the exemption side is the <em>absence</em> of a <c>Fab</c> component
/// on it. <c>OverlayRevisionPublishedDomainEvent</c> and
/// <c>OverlayRevisionArchivedDomainEvent</c> are exempt because ADR-0115 is
/// already expressed in their types, not because anyone typed their names here.
/// There is no exemption list to maintain and nothing to update when a handler
/// is added.
/// </para>
///
/// <para>
/// <b>What a green run does NOT prove</b> (spec 082 FR-007) — stated because a
/// guard whose greenness is read as more than it is becomes the record nobody
/// re-checks:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Not that the fab is the right one.</b> <c>fab.Value</c> and
/// <c>someOtherFab.Value</c> are indistinguishable to a source scan. The
/// per-handler tests assert the value; this asserts only that one is passed.
/// </item>
/// <item>
/// <b>Not that a fab reaches the audit row at runtime.</b> A nullable fab that
/// is null at runtime — <c>StreamHealthChangedDomainEventHandler</c>'s
/// <c>Fab?.Value</c> — passes this cleanly. That is #2076, and it is uncovered.
/// </item>
/// <item>
/// <b>Not that the audit surface is scoped correctly.</b> That is the
/// integration test's job.
/// </item>
/// <item>
/// <b>Nothing about publishers outside a handler.</b> The scan is anchored on
/// <c>Handle</c>/<c>HandleAsync</c> methods, so
/// <c>AuditRetentionHostedService</c> — which publishes from a private
/// archive-and-drop method — is outside it by construction. It is correct
/// today and it is not protected. (<c>RotateWebhookClientCommandHandler</c>
/// publishes from a <c>HandleAsync</c> and so is in scope, despite being a
/// command handler rather than an event handler.)
/// </item>
/// </list>
/// </summary>
public class EventMetadataFabDeclarationTests
{
    /// <summary>
    /// A <c>Handle</c>/<c>HandleAsync</c> method header, up to and including
    /// the parameter list's opening parenthesis. Deliberately looser than
    /// <see cref="HandlerDeconstructionTests"/>'s: the four CameraCatalog
    /// handlers are expression-bodied and non-async, and they are exactly the
    /// sites that already stamp the fab correctly.
    /// </summary>
    private static readonly Regex HandlerSignature = new(
        @"\b(?:public|internal)\s+(?:async\s+)?(?:Task|ValueTask)[^(\n]*?\s(?:Handle|HandleAsync)\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Every way an <c>EventMetadata</c> is constructed. Two spellings exist in
    /// <c>src/</c> and a scan for the explicit one alone misses the target-typed
    /// pair, so both alternatives are matched. The explicit alternative is tried
    /// first, so <c>Metadata: new EventMetadata(</c> is counted once.
    /// </summary>
    private static readonly Regex Construction = new(
        @"(?:Metadata\s*:\s*)?new\s+EventMetadata\s*\(|Metadata\s*:\s*new\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex NamedArgument = new(@"^[A-Za-z_]\w*\s*:(?!:)", RegexOptions.Compiled);

    [Fact]
    public void A_handler_whose_record_carries_a_fab_never_passes_a_literal_null_fab_to_EventMetadata()
    {
        Dictionary<string, string> sources = ReadSources();
        Dictionary<string, IReadOnlyList<string>?> records = new(StringComparer.Ordinal);
        MetadataShape shape = ShapeOf(sources, records);

        List<string> failures = [];
        int checkedCount = 0;

        foreach ((string path, string text) in sources)
        {
            foreach ((string typeName, string body, int offset) in Handlers(text))
            {
                foreach (Match construction in Construction.Matches(body))
                {
                    checkedCount++;
                    failures.AddRange(Inspect(
                        Where(path, text, offset + construction.Index),
                        typeName, body, construction, shape, sources, records));
                }
            }

            failures.AddRange(Unscanned(path, text));
        }

        checkedCount.ShouldBeGreaterThan(
            0, "no EventMetadata construction was found inside a handler — the scan is broken, not the code");
        failures.ShouldBeEmpty(string.Join(Environment.NewLine + Environment.NewLine, failures));
    }

    /// <summary>
    /// Reports any <c>EventMetadata</c> construction that sits in a file which
    /// declares a handler but was not reached by the handler scan. A guard that
    /// silently passes what it did not understand is the failure mode this
    /// repository has recorded before (spec 082 FR-005); a handler shape the
    /// signature regex does not match must announce itself rather than vanish.
    /// A file with no handler at all is out of scope by construction and is not
    /// reported here — see the class remarks.
    /// </summary>
    private static IEnumerable<string> Unscanned(string path, string text)
    {
        int declared = Construction.Count(text);
        int reached = Handlers(text).Sum(handler => Construction.Count(handler.Body));
        if (declared == reached || !HandlerSignature.IsMatch(text))
        {
            yield break;
        }

        yield return $"{path}: {declared} EventMetadata construction(s) in a file that declares a handler, "
            + $"but only {reached} sit inside a scanned handler body. The signature scan missed a handler "
            + "shape; widen it rather than leaving the site unchecked.";
    }

    private static IEnumerable<string> Inspect(
        string where,
        string typeName,
        string body,
        Match construction,
        MetadataShape shape,
        IReadOnlyDictionary<string, string> sources,
        Dictionary<string, IReadOnlyList<string>?> records)
    {
        string? arguments = Balanced(body, construction.Index + construction.Length - 1, '(', ')');
        if (arguments is null)
        {
            yield return $"{where}: an EventMetadata argument list has no closing parenthesis.";
            yield break;
        }

        string[] passed = [.. SplitTopLevel(arguments).Select(argument => argument.Trim())];
        if (passed.Length < shape.Minimum || passed.Length > shape.Maximum)
        {
            yield return $"{where}: EventMetadata was passed {passed.Length} arguments; its header takes "
                + $"{shape.Minimum} to {shape.Maximum}. The fab position cannot be read positionally.";
            yield break;
        }

        if (passed.Any(argument => NamedArgument.IsMatch(argument)))
        {
            yield return $"{where}: EventMetadata is constructed with named arguments, which this positional "
                + "scan cannot read. Use the positional form, or extend the guard.";
            yield break;
        }

        IReadOnlyList<string>? components = Components(typeName, sources, records);
        if (components is null)
        {
            yield return $"{where}: the record '{typeName}' this handler receives could not be found, so "
                + "whether it carries a fab cannot be derived.";
            yield break;
        }

        // The derivation: no Fab component on the source record means the event
        // genuinely has no fab, and a null here is correct. No exemption list.
        if (passed[shape.FabPosition] != "null" || !components.Contains("Fab", StringComparer.Ordinal))
        {
            yield break;
        }

        yield return $"{where}: '{typeName}' declares a Fab component, but this handler passes a literal null "
            + $"in EventMetadata's fab position (argument {shape.FabPosition}). The audit row it produces "
            + "records no fab, and a null-fab row is readable by every operator of every fab (#1300). "
            + "Pass the fab the record carries.";
    }

    /// <summary>
    /// Every <c>Handle</c>/<c>HandleAsync</c> in <paramref name="text"/> as its
    /// first parameter's type name, its body, and the body's offset in the file.
    /// </summary>
    private static List<(string TypeName, string Body, int Offset)> Handlers(string text)
    {
        List<(string, string, int)> handlers = [];
        foreach (Match signature in HandlerSignature.Matches(text))
        {
            int open = signature.Index + signature.Length - 1;
            string? parameters = Balanced(text, open, '(', ')');
            if (parameters is null)
            {
                continue;
            }

            string first = SplitTopLevel(parameters).First().Trim();
            string[] words = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2)
            {
                continue;
            }

            (string Body, int Offset)? body = BodyAfter(text, open + parameters.Length + 2);
            if (body is not null)
            {
                handlers.Add((words[^2], body.Value.Body, body.Value.Offset));
            }
        }

        return handlers;
    }

    /// <summary>
    /// The method body starting at <paramref name="afterParameters"/>: braces
    /// for a block body, everything up to the terminating semicolon for an
    /// expression body. Locating it from the parameter list rather than from
    /// "the next open brace" is what lets the scan read the expression-bodied
    /// handlers without mis-attributing the following method's body to them.
    /// </summary>
    private static (string Body, int Offset)? BodyAfter(string text, int afterParameters)
    {
        int i = afterParameters;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (i < text.Length && text[i] == '{')
        {
            string? block = Balanced(text, i, '{', '}');
            return block is null ? null : (block, i + 1);
        }

        if (i + 1 >= text.Length || text[i] != '=' || text[i + 1] != '>')
        {
            return null;
        }

        int start = i + 2;
        int end = EndOfStatement(text, start);
        return end < 0 ? null : (text[start..end], start);
    }

    /// <summary>Index of the semicolon closing an expression body, or -1.</summary>
    private static int EndOfStatement(string text, int start)
    {
        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == ';' && depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// <c>EventMetadata</c>'s own shape, read from its record header rather than
    /// written down: the fab's position, and the arities the header admits.
    /// A guard that hard-coded "position 2, arity 4 or 5" would go quietly wrong
    /// the day a component is inserted before <c>Fab</c>.
    /// </summary>
    private static MetadataShape ShapeOf(
        IReadOnlyDictionary<string, string> sources, Dictionary<string, IReadOnlyList<string>?> records)
    {
        string? header = HeaderOf("EventMetadata", sources);
        header.ShouldNotBeNull("EventMetadata's record header could not be found — the scan is broken.");

        string[] parameters = [.. SplitTopLevel(header).Select(p => p.Trim()).Where(p => p.Length > 0)];
        List<string> names = Names(header);
        records["EventMetadata"] = names;

        int fab = -1;
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], "Fab", StringComparison.Ordinal))
            {
                fab = i;
            }
        }

        fab.ShouldBeGreaterThanOrEqualTo(0, "EventMetadata declares no Fab component — this guard is obsolete.");
        return new MetadataShape(
            fab, parameters.Count(p => !p.Contains('=', StringComparison.Ordinal)), parameters.Length);
    }

    /// <summary>The positional component names of <paramref name="typeName"/>, or null.</summary>
    private static IReadOnlyList<string>? Components(
        string typeName,
        IReadOnlyDictionary<string, string> sources,
        Dictionary<string, IReadOnlyList<string>?> cache)
    {
        if (cache.TryGetValue(typeName, out IReadOnlyList<string>? cached))
        {
            return cached;
        }

        string? header = HeaderOf(typeName, sources);
        IReadOnlyList<string>? components = header is null ? null : Names(header);
        cache[typeName] = components;
        return components;
    }

    private static string? HeaderOf(string typeName, IReadOnlyDictionary<string, string> sources)
    {
        Regex declaration = new(@"\brecord\s+(?:class\s+|struct\s+)?" + Regex.Escape(typeName) + @"\s*\(");
        foreach (string text in sources.Values)
        {
            Match match = declaration.Match(text);
            if (match.Success)
            {
                return Balanced(text, match.Index + match.Length - 1, '(', ')');
            }
        }

        return null;
    }

    /// <summary>Parameter names, dropping type, modifiers and any default value.</summary>
    private static List<string> Names(string parameters) =>
        SplitTopLevel(parameters)
            .Select(p => p.Split('=')[0].Trim())
            .Where(p => p.Length > 0)
            .Select(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1])
            .ToList();

    /// <summary>Splits an argument list on commas that are not nested.</summary>
    private static IEnumerable<string> SplitTopLevel(string arguments)
    {
        int depth = 0;
        int start = 0;
        for (int i = 0; i < arguments.Length; i++)
        {
            char c = arguments[i];
            if (c is '<' or '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is '>' or ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                yield return arguments[start..i];
                start = i + 1;
            }
        }

        yield return arguments[start..];
    }

    /// <summary>Contents between <paramref name="openIndex"/> and its match.</summary>
    private static string? Balanced(string text, int openIndex, char open, char close)
    {
        int depth = 0;
        for (int i = openIndex; i < text.Length; i++)
        {
            if (text[i] == open)
            {
                depth++;
            }
            else if (text[i] == close)
            {
                depth--;
                if (depth == 0)
                {
                    return text[(openIndex + 1)..i];
                }
            }
        }

        return null;
    }

    private static string Where(string path, string text, int index) =>
        $"{path}:{text.AsSpan(0, Math.Min(index, text.Length)).Count('\n') + 1}";

    private static Dictionary<string, string> ReadSources()
    {
        DirectoryInfo? candidate = new(AppContext.BaseDirectory);
        while (candidate is not null && !File.Exists(Path.Combine(candidate.FullName, "SmartSentinelEye.slnx")))
        {
            candidate = candidate.Parent;
        }

        DirectoryInfo root = candidate
            ?? throw new InvalidOperationException(
                $"could not locate the repository root above {AppContext.BaseDirectory}");

        string src = Path.Combine(root.FullName, "src");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(root.FullName, f).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);
    }

    private readonly record struct MetadataShape(int FabPosition, int Minimum, int Maximum);
}
