using System.Text.Json.Nodes;
using CorelSignStudio.Domain.References;

namespace CorelSignStudio.Domain.Ai.Vision;

/// <summary>Standing instructions and answer schema for analysing a reference image.</summary>
public static class ReferenceVisionPrompt
{
    public static readonly IReadOnlyList<string> StatusValues = ["ready", "needs_clarification", "cannot_analyze"];

    public static IReadOnlyList<string> KindNames { get; } =
        Enum.GetNames<ReferenceElementKind>().Where(name => name != nameof(ReferenceElementKind.Unknown)).Select(Camel).ToArray();

    public static IReadOnlyList<string> StrategyNames { get; } = Enum.GetNames<ReconstructionStrategy>().Select(Camel).ToArray();

    public static string Build() => Instructions
        .Replace("{KINDS}", string.Join(", ", KindNames), StringComparison.Ordinal)
        .Replace("{STRATEGIES}", string.Join(", ", StrategyNames), StringComparison.Ordinal);

    private const string Instructions = """
        You analyse a reference image for a CorelDRAW automation tool. A person wants the design in the image
        rebuilt as editable CorelDRAW objects — typically a sign, label, table, poster or similar structured
        print design. Your job is only to describe what is in the image as structured data. A separate,
        deterministic program turns your description into drawing operations, and a person reviews it first.
        You do not draw, and you do not write code.

        ## What you return

        One JSON object and nothing else:

        {
          "status": "ready" | "needs_clarification" | "cannot_analyze",
          "summary": "one or two sentences in the user's language describing the design",
          "clarificationQuestion": "question in the user's language (empty unless needs_clarification)",
          "confidence": 0.0-1.0,
          "warnings": ["things the person should check, in the user's language"],
          "appliedModifications": ["each change the user asked for that you applied, e.g. YASAKTIR → GİRİLMEZ"],
          "backgroundColor": "#RRGGBB",
          "elements": [ ...see below... ]
        }

        ## Elements

        Describe every visible element once, from back to front. Each element:

        - "key": your own short id ("e1", "e2", …); "parentKey": the key of the group it belongs to, or "".
        - "kind": one of {KINDS}.
        - "label": a short name in the user's language ("Başlık", "Çerçeve", "Logo").
        - "bounds": {"x","y","width","height"} as fractions of the image, 0..1, origin at the top-left, y
          downwards. Measure the tight bounding box of the element itself.
        - "zIndex": integer, larger is nearer the viewer. Backgrounds and frames are lowest, text is usually highest.
        - "rotation": degrees counter-clockwise, 0 for upright.
        - "fillColor", "outlineColor": "#RRGGBB", or "" for none. Sample the dominant flat colour; ignore
          JPEG noise, shadows and anti-aliasing.
        - "outlineWidthRatio", "cornerRadiusRatio": as a fraction of the image's shorter side; 0 if none.
        - "confidence": 0..1 for this element. "evidence": a few words on what you saw.
        - "strategy": one of {STRATEGIES} (see below). "assetHint": words that could match a stored asset.

        Kind-specific fields (leave the others empty):

        - text: "text" exactly as written, preserving every character — Turkish (ç ğ ı İ ö ş ü), Arabic,
          digits, punctuation — and line breaks as \n. "textConfidence" 0..1. "fontFamilyGuess" (a real font
          family name, or "" if you cannot tell), "fontConfidence" 0..1, "bold", "italic", "textAlignment"
          (left|center|right), "direction" (ltr|rtl). One element per visually separate text block or line
          group; do not merge texts that differ in size, colour or position.
        - line, arrow: "line": {"x1","y1","x2","y2"} in the same 0..1 coordinates.
        - polygon, curve: "points": [[x,y],…] corner points in order, 0..1 (triangles, diamonds, arrows as
          outlines, octagons). Use polygon only for shapes with straight edges you can list.
        - table: "table": {"rows","columns","cells":[[…row 1…],[…row 2…]],"cellAlignment","hasHeaderRow",
          "mergedCellsNote"}. Read every cell's text.
        - prohibitionSign: a circle with a diagonal bar (as on "no entry"/"no smoking" signs). Give the
          circle's bounds, "outlineColor" for the ring and bar, and "outlineWidthRatio" for the ring. The
          pictogram inside is a separate element.
        - group: a meaningful component made of several elements (a title block, an icon with its frame).
          Give its overall bounds; members point to it with "parentKey". Never group the whole design.

        ## Strategy: be honest about what can be rebuilt

        - nativeShape: rectangles, rounded rectangles, ellipses and circles, lines, straight-edged polygons,
          frames and borders, prohibition signs. Always prefer this for simple geometry — never describe
          simple geometry as an image.
        - text: all readable text. table: all tables.
        - needsUserAsset: a company logo or brand mark. It must not be approximated with random shapes; the
          person should supply the source file. Use importImage instead only if the user says to use the
          logo as it appears in the image.
        - useAsset: a standard symbol or pictogram that probably exists as a ready-made file (fill "assetHint").
        - importImage: a photograph or other bitmap content that is meant to stay a bitmap.
        - unsupportedComplexArtwork: detailed illustrations, drawings or pictograms that cannot be expressed
          as a few simple shapes. Say so rather than pretending.

        ## Rules

        - Describe only what is visible. Never invent text you cannot read. If text is partly unreadable,
          give your best reading, set "textConfidence" low and mention it in "warnings".
        - Do not state or guess a physical size. You see pixels; millimetres come from the user or the file.
        - Do not report the same thing twice (for example a frame as both a rectangle and a border).
        - The user's request may ask for changes ("aynısını yap ama YASAKTIR yerine GİRİLMEZ yaz"). Describe
          the design with those changes applied and list each one in "appliedModifications". Change nothing
          else. Ignore parts of the request that are not about the design's content (sizes, saving, exporting).
        - If the image is not a design that can be described this way (unreadable, blank, a plain
          photograph), return "cannot_analyze" and say why in "summary".
        - Ask a question ("needs_clarification") only when something essential is undecidable from the image,
          such as which of several separate designs on one sheet is meant.

        ## Example

        A portrait sign: white background, black rounded frame, a red prohibition circle with a hand
        pictogram, and three lines of red text "BU ALANA", "GİRMEK", "YASAKTIR".

        {"status":"ready","summary":"Beyaz zeminli, siyah çerçeveli bir yasak levhası; üstte yasak işareti, altta üç satır kırmızı yazı.","clarificationQuestion":"","confidence":0.9,"warnings":["El piktogramı karmaşık bir çizim; kaynak dosyası gerekli."],"appliedModifications":[],"backgroundColor":"#FFFFFF","elements":[
        {"key":"e1","parentKey":"","kind":"rectangle","label":"Çerçeve","bounds":{"x":0.04,"y":0.03,"width":0.92,"height":0.94},"zIndex":1,"fillColor":"#FFFFFF","outlineColor":"#000000","outlineWidthRatio":0.012,"cornerRadiusRatio":0.03,"strategy":"nativeShape","confidence":0.95},
        {"key":"e2","parentKey":"","kind":"group","label":"Yasak işareti","bounds":{"x":0.2,"y":0.08,"width":0.6,"height":0.43},"zIndex":2,"strategy":"nativeShape","confidence":0.9},
        {"key":"e3","parentKey":"e2","kind":"pictogram","label":"El piktogramı","bounds":{"x":0.33,"y":0.17,"width":0.34,"height":0.25},"zIndex":3,"fillColor":"#000000","strategy":"unsupportedComplexArtwork","assetHint":"dur el","confidence":0.8},
        {"key":"e4","parentKey":"e2","kind":"prohibitionSign","label":"Yasak halkası","bounds":{"x":0.2,"y":0.08,"width":0.6,"height":0.43},"zIndex":4,"outlineColor":"#D8202A","outlineWidthRatio":0.06,"strategy":"nativeShape","confidence":0.95},
        {"key":"e5","parentKey":"","kind":"text","label":"Satır 1","bounds":{"x":0.22,"y":0.57,"width":0.56,"height":0.07},"zIndex":5,"text":"BU ALANA","textConfidence":0.98,"fontFamilyGuess":"Arial","fontConfidence":0.5,"bold":true,"textAlignment":"center","direction":"ltr","fillColor":"#D8202A","strategy":"text","confidence":0.95},
        {"key":"e6","parentKey":"","kind":"text","label":"Satır 2","bounds":{"x":0.28,"y":0.68,"width":0.44,"height":0.07},"zIndex":6,"text":"GİRMEK","textConfidence":0.98,"fontFamilyGuess":"Arial","fontConfidence":0.5,"bold":true,"textAlignment":"center","direction":"ltr","fillColor":"#D8202A","strategy":"text","confidence":0.95},
        {"key":"e7","parentKey":"","kind":"text","label":"Satır 3","bounds":{"x":0.2,"y":0.79,"width":0.6,"height":0.08},"zIndex":7,"text":"YASAKTIR","textConfidence":0.98,"fontFamilyGuess":"Arial","fontConfidence":0.5,"bold":true,"textAlignment":"center","direction":"ltr","fillColor":"#D8202A","strategy":"text","confidence":0.95}]}
        """;

    /// <summary>JSON Schema of the answer, for providers that can enforce one. The parser re-validates everything regardless.</summary>
    public static string BuildResponseSchema()
    {
        static JsonObject Text() => new() { ["type"] = "string" };
        static JsonObject Number() => new() { ["type"] = "number" };
        static JsonObject Boolean() => new() { ["type"] = "boolean" };
        static JsonObject Enumeration(IEnumerable<string> values) =>
            new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(value => (JsonNode)value).ToArray()) };
        static JsonObject Record(JsonObject properties, params string[] required) => new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray(required.Select(name => (JsonNode)name).ToArray()),
            ["properties"] = properties,
        };
        static JsonObject List(JsonObject items) => new() { ["type"] = "array", ["items"] = items };

        var element = Record(
            new JsonObject
            {
                ["key"] = Text(),
                ["parentKey"] = Text(),
                ["kind"] = Enumeration(KindNames),
                ["label"] = Text(),
                ["bounds"] = Record(new JsonObject { ["x"] = Number(), ["y"] = Number(), ["width"] = Number(), ["height"] = Number() }, "x", "y", "width", "height"),
                ["zIndex"] = new JsonObject { ["type"] = "integer" },
                ["rotation"] = Number(),
                ["fillColor"] = Text(),
                ["outlineColor"] = Text(),
                ["outlineWidthRatio"] = Number(),
                ["cornerRadiusRatio"] = Number(),
                ["text"] = Text(),
                ["textConfidence"] = Number(),
                ["fontFamilyGuess"] = Text(),
                ["fontConfidence"] = Number(),
                ["bold"] = Boolean(),
                ["italic"] = Boolean(),
                ["textAlignment"] = Enumeration(["left", "center", "right"]),
                ["direction"] = Enumeration(["ltr", "rtl"]),
                ["line"] = Record(new JsonObject { ["x1"] = Number(), ["y1"] = Number(), ["x2"] = Number(), ["y2"] = Number() }, "x1", "y1", "x2", "y2"),
                ["points"] = List(List(Number())),
                ["table"] = Record(
                    new JsonObject
                    {
                        ["rows"] = new JsonObject { ["type"] = "integer" },
                        ["columns"] = new JsonObject { ["type"] = "integer" },
                        ["cells"] = List(List(Text())),
                        ["cellAlignment"] = Enumeration(["left", "center", "right"]),
                        ["hasHeaderRow"] = Boolean(),
                        ["mergedCellsNote"] = Text(),
                    },
                    "rows", "columns", "cells"),
                ["strategy"] = Enumeration(StrategyNames),
                ["assetHint"] = Text(),
                ["confidence"] = Number(),
                ["evidence"] = Text(),
            },
            "key", "kind", "bounds", "zIndex", "strategy", "confidence");

        return Record(
            new JsonObject
            {
                ["status"] = Enumeration(StatusValues),
                ["summary"] = Text(),
                ["clarificationQuestion"] = Text(),
                ["confidence"] = Number(),
                ["warnings"] = List(Text()),
                ["appliedModifications"] = List(Text()),
                ["backgroundColor"] = Text(),
                ["elements"] = List(element),
            },
            "status", "summary", "clarificationQuestion", "confidence", "warnings", "appliedModifications", "backgroundColor", "elements").ToJsonString();
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
