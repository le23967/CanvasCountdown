import Foundation

/// An answer from the model, made fit to put on screen.
///
/// Models write Markdown whether or not they are asked to. The panel used to
/// show a reply exactly as it arrived, so a word the model meant to stress came
/// out with a pair of asterisks either side of it. What is kept in the
/// conversation is still the reply as written; this only decides how it is
/// drawn.
enum AssistantReplyText {
    /// The reply with its emphasis, code and links drawn rather than spelled
    /// out, and its line breaks exactly where the model put them.
    ///
    /// Only what sits inside a line is interpreted. A reply is a short answer
    /// in a narrow column, not a document, and laying one out as blocks would
    /// move its lines around. The two block marks models reach for most are
    /// tidied by hand instead: a bullet becomes a bullet and a heading becomes
    /// a line in bold.
    ///
    /// A reply that cannot be read as Markdown is shown as it was sent.
    static func rendered(_ reply: String) -> AttributedString {
        let options = AttributedString.MarkdownParsingOptions(
            allowsExtendedAttributes: false,
            interpretedSyntax: .inlineOnlyPreservingWhitespace,
            failurePolicy: .returnPartiallyParsedIfPossible
        )
        guard var rendered = try? AttributedString(
            markdown: preparedForParsing(reply),
            options: options
        ) else {
            return AttributedString(reply)
        }
        // Web pages only. A reply is text from a model, and a link in it to
        // a file on this Mac or to some other app's scheme is not something
        // to put one click away.
        let otherLinks = rendered.runs.compactMap { run -> Range<AttributedString.Index>? in
            guard let link = run.link else {
                return nil
            }
            let scheme = link.scheme?.lowercased()
            return scheme == "http" || scheme == "https" ? nil : run.range
        }
        for range in otherLinks {
            rendered[range].link = nil
        }
        return rendered
    }

    /// The reply, made safe to read as Markdown without losing any of it.
    ///
    /// Two things models write all the time are Markdown by accident: a range
    /// written with a tilde, "2~3小时", which the parser took as the start of a
    /// strikethrough and swallowed the tildes of, and a product written without
    /// spaces, "5*3*2", which became an italic 3. Both are escaped so they are
    /// shown as written. And a bold run ending in Chinese punctuation,
    /// "**截止时间：**明天", is not allowed to close by the rules of Markdown, so
    /// its asterisks showed; the punctuation is moved just outside it.
    static func preparedForParsing(_ reply: String) -> String {
        tidyingBlockMarks(in: reply)
            .split(separator: "\n", omittingEmptySubsequences: false)
            .map { escapingOutsideCode(String($0)) }
            .joined(separator: "\n")
    }

    private static func escapingOutsideCode(_ line: String) -> String {
        // Even pieces are outside `code`, where nothing is to be changed.
        line
            .split(separator: "`", omittingEmptySubsequences: false)
            .enumerated()
            .map { index, piece in
                guard index.isMultiple(of: 2) else {
                    return String(piece)
                }
                var text = String(piece)
                text = text.replacingOccurrences(
                    of: "\\*\\*([（「『【《“]?)([^*\\n]+?)([：，。；、！？）」』】》”]?)\\*\\*",
                    with: "$1**$2**$3",
                    options: .regularExpression
                )
                text = text.replacingOccurrences(of: "~", with: "\\~")
                text = text.replacingOccurrences(
                    of: "(?<=\\d)\\*(?=\\d)",
                    with: "\\\\*",
                    options: .regularExpression
                )
                return text
            }
            .joined(separator: "`")
    }

    /// Turns `- item` into `• item` and `## Title` into `**Title**`, a line at
    /// a time, leaving everything else as it was.
    static func tidyingBlockMarks(in reply: String) -> String {
        reply
            .replacingOccurrences(of: "\r\n", with: "\n")
            .split(separator: "\n", omittingEmptySubsequences: false)
            .map { tidied(String($0)) }
            .joined(separator: "\n")
    }

    private static func tidied(_ line: String) -> String {
        let indent = String(line.prefix(while: { $0 == " " || $0 == "\t" }))
        let body = line.dropFirst(indent.count)

        // "- ", "* " or "+ " and then something: a list item. The space is
        // what tells it from "-5" or "*emphasis*", and the something is what
        // tells it from a rule drawn as "- - -".
        if let mark = body.first, "-*+".contains(mark) {
            let rest = body.dropFirst()
            if rest.first == " ",
               !rest.allSatisfy({ $0 == " " || $0 == mark }) {
                return indent + "• " + String(rest.drop(while: { $0 == " " }))
            }
        }

        // One to six hashes, a space, and a title.
        let hashes = body.prefix(while: { $0 == "#" })
        if (1...6).contains(hashes.count) {
            let rest = body.dropFirst(hashes.count)
            if rest.first == " " {
                let title = rest.trimmingCharacters(in: .whitespaces)
                guard !title.isEmpty else {
                    return line
                }
                // Already marked up by the model: bold around it would be
                // read as the end of its own emphasis rather than the start.
                let isPlain = !title.contains(where: { "*_`".contains($0) })
                return indent + (isPlain ? "**\(title)**" : title)
            }
        }
        return line
    }

    /// The part of a reply that was meant to be read.
    ///
    /// Some models think aloud inside `<think>` tags before answering. That is
    /// working, not an answer: shown, it buries the reply under a paragraph of
    /// deliberation, and left in a reply that should be JSON it is what the
    /// reader trips over first. A block that was opened and never closed is the
    /// model running out of room mid-thought, so nothing after it is an answer
    /// either.
    static func withoutReasoning(_ reply: String) -> String {
        var remaining = Substring(reply)
        var kept = ""
        while let open = remaining.range(
            of: "<think>",
            options: .caseInsensitive
        ) {
            kept += remaining[..<open.lowerBound]
            guard let close = remaining[open.upperBound...].range(
                of: "</think>",
                options: .caseInsensitive
            ) else {
                remaining = ""
                break
            }
            remaining = remaining[close.upperBound...]
        }
        kept += remaining
        // Some models are started inside their thinking by the prompt, and
        // send only the closing tag: everything before it is working.
        if let close = kept.range(
            of: "</think>",
            options: [.caseInsensitive, .backwards]
        ) {
            kept = String(kept[close.upperBound...])
        }
        return kept.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
