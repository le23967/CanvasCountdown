import CoreGraphics
import Foundation

/// Where the user asked the assistant to appear.
///
/// There is no automatic mode. Choosing the container from the window width
/// meant a tiled or split-screen window silently lost the sidebar, which is the
/// one case where the panel is most wanted and least replaceable. The width now
/// decides how wide the panel is, never whether it exists.
enum AssistantPresentationPreference: String, Codable, CaseIterable, Identifiable, Sendable {
    case sidebar
    case popover

    var id: String { rawValue }

    var title: String {
        switch self {
        case .sidebar:
            "Sidebar"
        case .popover:
            "Popover"
        }
    }

    /// Anything unrecognised — notably the "automatic" that used to be stored —
    /// reads as a sidebar, so an old preferences file opens on the new default
    /// rather than throwing the whole assistant blob back to its defaults.
    init(from decoder: any Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = AssistantPresentationPreference(rawValue: raw) ?? .sidebar
    }
}

/// What is actually on screen. Exactly one at a time.
///
/// A single value rather than a handful of booleans: with `isSidebarOpen`,
/// `isPopoverOpen` and `isWindowOpen` it is possible to be in two places at
/// once, and every close path has to remember to clear all three.
enum ActiveAssistantPresentation: String, Equatable, Sendable {
    case closed
    case sidebar
    case popover
    case separateWindow

    var isOpen: Bool {
        self != .closed
    }
}

/// What the one box in the assistant is for.
///
/// There used to be three fields — ask, describe a task, change these drafts —
/// stacked one above another, each with its own button. The third read as the
/// save field, which meant the one place a typed sentence could be thrown away
/// by pressing the wrong thing. One box that says what it is doing replaces all
/// three.
///
/// `automatic` is the default so that the box costs nothing to use: most
/// sentences say plainly enough which of the two they are, and the panel shows
/// what it read before anything is sent. The other two are there for when that
/// guess is not wanted — choosing `ask` means a sentence is never read as a
/// task, however much it sounds like one.
enum AssistantComposerMode: String, Codable, CaseIterable, Identifiable, Sendable {
    case automatic
    case ask
    case addTask

    var id: String { rawValue }

    /// Kept to two words: this is the label on a chip under the field, not a
    /// sentence.
    var title: String {
        switch self {
        case .automatic:
            "Automatic"
        case .ask:
            "Ask"
        case .addTask:
            "Add task"
        }
    }

    var systemImage: String {
        switch self {
        case .automatic:
            "wand.and.stars"
        case .ask:
            "bubble.left.and.text.bubble.right"
        case .addTask:
            "calendar.badge.plus"
        }
    }
}

/// What the box will actually do when Return is pressed right now.
///
/// Not the same as the mode: drafts on screen take the box over, because a
/// sentence typed while a review is open is about that review, and on automatic
/// the sentence itself decides.
enum AssistantComposerRole: Equatable, Sendable {
    case ask
    case addTask
    case reviseDrafts
}

/// Reads a sentence and says which of the two it is.
///
/// Deliberately local and deliberately plain. Sending the sentence to the model
/// to be classified would cost a round trip before anything happened, and would
/// make a box that is meant to feel free to type in the slowest part of the
/// panel. Neither answer is expensive to be wrong about: a question produces an
/// answer, and a task produces a review that writes nothing until it is
/// confirmed. The panel says which way it read the sentence while it is still
/// being typed, so a wrong guess is corrected before it is sent, not after.
enum AssistantRequestReader {
    /// Words that open a question about the list rather than name a thing to
    /// put on it. "Should I start the essay today" has no question mark and is
    /// still plainly a question.
    private static let questionOpeners: Set<String> = [
        "am", "are", "can", "could", "did", "do", "does", "explain", "has",
        "have", "how", "is", "list", "must", "shall", "should", "show",
        "summarise", "summarize", "tell", "was", "were", "what", "whats",
        "when", "where", "which", "who", "why", "will", "would",
    ]

    /// Verbs that ask for something to be put on the list.
    private static let addingOpeners: Set<String> = [
        "add", "book", "create", "make", "new", "note", "plan", "put",
        "remind", "schedule", "set",
    ]

    /// Words that only appear when a sentence is naming a time.
    private static let timeWords: Set<String> = [
        "today", "tonight", "tomorrow", "monday", "tuesday", "wednesday",
        "thursday", "friday", "saturday", "sunday", "mon", "tue", "tues",
        "wed", "thu", "thur", "thurs", "fri", "sat", "sun", "january",
        // "may" is left out on purpose: it is a modal verb far more often than
        // it is a month, and a date in May still carries its day number.
        "february", "march", "april", "june", "july", "august",
        "september", "october", "november", "december", "jan", "feb", "mar",
        "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
        "am", "pm", "noon", "midnight", "morning", "afternoon", "evening",
        "weekly", "fortnightly", "daily",
    ]

    static func readsAsQuestion(_ text: String) -> Bool {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            // An empty box has not said anything yet, and asking is what most
            // people open the panel to do.
            return true
        }
        if trimmed.hasSuffix("?") || trimmed.hasSuffix("？") {
            return true
        }

        let words = trimmed.lowercased().split { !$0.isLetter && !$0.isNumber }
            .map(String.init)
        guard let first = words.first else {
            return true
        }
        if questionOpeners.contains(first) {
            return true
        }
        if addingOpeners.contains(first) {
            return false
        }
        // Chinese is written without spaces, so it has no first word to look
        // up and none of the lists above ever matched it: every sentence in
        // it was read as a question, and "remind me to do the laundry at one"
        // was answered with an explanation instead of being drafted.
        let lowered = trimmed.lowercased()
        if let reading = ChineseRequest.reading(of: lowered) {
            return reading
        }
        // Nothing said when. Whatever this is, it cannot become a task with a
        // due date, so it goes to the side that can answer it.
        return !(ChineseRequest.namesATime(lowered)
            || namesATime(lowered, words: words))
    }

    private static func namesATime(_ text: String, words: [String]) -> Bool {
        if words.contains(where: timeWords.contains) {
            return true
        }
        // A clock time written as one word: 5pm, 11am.
        if words.contains(where: { word in
            (word.hasSuffix("am") || word.hasSuffix("pm"))
                && word.dropLast(2).allSatisfy(\.isNumber)
                && !word.dropLast(2).isEmpty
        }) {
            return true
        }
        // A bare number, but only one small enough to be a day or an hour. A
        // course code is a run of digits too, and "anything for 41021" is a
        // question about the list, not a task due in the year 41021.
        if words.contains(where: { word in
            word.allSatisfy(\.isNumber) && (1...2).contains(word.count)
        }) {
            return true
        }
        // 26/8, 17:00, 26.8 — the separator is what makes these a date or a
        // time rather than two unrelated numbers.
        return text.indices.contains { index in
            guard text[index] == ":" || text[index] == "/" else {
                return false
            }
            let after = text.index(after: index)
            guard index > text.startIndex, after < text.endIndex else {
                return false
            }
            return text[text.index(before: index)].isNumber
                && text[after].isNumber
        }
    }
}

/// The same reading, for a sentence written in Chinese.
///
/// Kept apart from the English lists because the questions are the same and
/// the way to ask them is not: with no spaces there are no words to look up, so
/// this looks for how a sentence opens, how it ends and what it contains.
/// Simplified and traditional spellings are both listed, since the same person
/// types either depending on the keyboard in front of them.
private enum ChineseRequest {
    /// Courtesies and lead-ins that come before the verb and say nothing about
    /// which kind of sentence follows.
    private static let leadIns = [
        "请问", "請問", "请", "請", "麻烦你", "麻煩你", "麻烦", "麻煩",
        "能不能", "能否", "可不可以", "可以", "可否", "能够", "能夠", "能",
        "你", "帮我", "幫我", "帮忙", "幫忙", "给我", "給我", "替我",
        "我想要", "我需要", "我想", "我要", "再", "另外", "然后", "然後",
        "顺便", "順便",
    ]

    /// Verbs that ask for something to be put on the list.
    private static let addingOpeners = [
        "提醒", "添加", "新增", "新建", "创建", "創建", "加一个", "加一個",
        "加个", "加個", "加一项", "加一項", "加上", "加入", "记一下", "記一下",
        "记下", "記下", "记个", "記個", "安排一下", "安排个", "安排個",
        "预约", "預約", "录入", "錄入", "登记", "登記", "备忘", "備忘",
    ]

    /// Verbs that ask to be told something about the list.
    private static let askingOpeners = [
        "列出", "列一下", "告诉我", "告訴我", "说说", "說說", "讲讲", "講講",
        "看看", "看一下", "看下", "查看", "查一下", "查查", "查询", "查詢",
    ]

    /// Verbs that ask to be told something, and are also the first half of a
    /// noun: 统计 is "count up" and the start of 统计学, 总结 is "sum up" and
    /// the start of 总结报告. They only count as asking when what follows
    /// says so, otherwise "统计学作业周五交" was read as a request for a count.
    private static let nounLikeAskingOpeners = [
        "总结", "總結", "概括", "汇总", "彙總", "列举", "列舉", "显示",
        "顯示", "解释", "解釋", "分析", "统计", "統計",
    ]

    /// What follows a noun-like verb when it is being used to ask.
    private static let askingContinuations = [
        "一下", "下", "我", "这", "這", "那", "今", "明", "本", "最近", "所有",
        "全部", "目前", "现在", "現在", "一共", "还", "還",
    ]

    /// A verb of asking somebody else, inside a task: "明天问老师作业怎么交"
    /// is something to do tomorrow, not a question for the assistant.
    private static let embeddedAskingVerbs = [
        "问", "問", "询问", "詢問", "确认", "確認", "确定", "確定", "打听",
        "打聽",
    ]

    /// "几个" is "how many" in a question and "a few" after a verb of getting
    /// or making: 买几个本子 is a shopping task.
    private static let fewMeasures = [
        "几个", "幾個", "几份", "幾份", "几项", "幾項", "几门", "幾門",
        "几本", "幾本", "几张", "幾張",
    ]
    private static let fewVerbs = [
        "买", "買", "拿", "带", "帶", "借", "打印", "印", "复印", "複印",
        "准备", "準備", "写", "寫", "做", "订", "訂", "换", "換", "领", "領",
    ]

    /// Particles that turn a statement into a question when they end it.
    private static let closingParticles = [
        "吗", "嗎", "呢", "么", "麼", "没有", "沒有", "了没", "了沒", "没",
        "沒",
    ]

    /// Words that only appear when something is being asked.
    private static let questionWords = [
        "什么", "什麼", "甚麼", "啥", "多少", "多久", "多长时间", "多長時間",
        "几个", "幾個", "几项", "幾項", "几份", "幾份", "几门", "幾門",
        "几点", "幾點", "几号", "幾號", "星期几", "星期幾", "周几", "週幾",
        "礼拜几", "禮拜幾", "还有几", "還有幾", "还剩几", "還剩幾",
        "哪些", "哪个", "哪個", "哪门", "哪門", "哪天", "哪一", "哪里",
        "哪裡", "哪儿", "哪兒", "怎么", "怎麼", "怎样", "怎樣", "如何",
        "为什么", "為什麼", "为何", "為何", "是否", "是不是", "有没有",
        "有沒有", "能不能", "能否", "可不可以", "要不要", "该不该", "該不該",
        "应不应该", "應不應該", "会不会", "會不會", "谁", "誰",
    ]

    /// Words that name a day or a part of one.
    ///
    /// "This week" is left out on purpose and "next week" is not: the first is
    /// how a question about the list is usually framed, the second is how a
    /// deadline is.
    private static let timeWords: [String] = {
        var words = [
            "今天", "今日", "今晚", "今早", "今夜", "明天", "明日", "明晚",
            "明早", "后天", "後天", "上午", "下午", "中午", "晚上", "早上",
            "凌晨", "傍晚", "半夜", "午夜", "清晨", "夜里", "夜裡", "周末",
            "週末", "下周", "下週", "下星期", "下礼拜", "下禮拜", "下个月",
            "下個月", "月底", "月初", "月末", "年底", "每天", "每晚", "每周",
            "每週", "每月", "天后", "天後", "天内", "天內", "天之内",
            "天之內", "周后", "週後", "星期后", "星期後", "小时后", "小時後",
            "分钟后", "分鐘後", "个月后", "個月後", "期末", "期中", "开学",
            "開學",
        ]
        for week in ["周", "週", "星期", "礼拜", "禮拜"] {
            for day in ["一", "二", "三", "四", "五", "六", "日", "天"] {
                words.append(week + day)
            }
        }
        return words
    }()

    /// The part of the day a clock time is said after: 下午一点.
    private static let dayParts = [
        "上午", "下午", "中午", "晚上", "早上", "凌晨", "傍晚", "半夜",
        "今晚", "明晚", "今早", "明早",
    ]

    private static let numerals = Set(
        "0123456789０１２３４５６７８９〇一二三四五六七八九十两兩"
    )
    private static let asciiDigits = Set("0123456789")
    private static let clockMarks = Set("点點")
    private static let dateMarks = Set("月号號日")
    private static let afterAClockTime = Set("半钟鐘整")

    /// Whether the sentence is a question, or `nil` when nothing here can
    /// tell — because it is not Chinese, or because it neither opens nor ends
    /// like either kind and only a time would settle it.
    static func reading(of text: String) -> Bool? {
        guard isChinese(text) else {
            return nil
        }
        var sentence = Substring(text)
        while let last = sentence.last, !last.isLetter, !last.isNumber {
            sentence.removeLast()
        }

        var opening = sentence
        var stripped = true
        while stripped {
            stripped = false
            for leadIn in leadIns where opening.hasPrefix(leadIn) {
                opening = opening.dropFirst(leadIn.count)
                stripped = true
                break
            }
        }

        // An instruction to add comes first, however it ends: "remind me to
        // ask how to submit it" contains a question word, and "提醒我周五交
        // 报告好吗" ends like a question, and both are things to write down.
        if addingOpeners.contains(where: opening.hasPrefix) {
            return false
        }
        if closingParticles.contains(where: sentence.hasSuffix) {
            return true
        }
        if askingOpeners.contains(where: opening.hasPrefix) {
            return true
        }
        if let verb = nounLikeAskingOpeners.first(where: opening.hasPrefix) {
            let rest = opening.dropFirst(verb.count)
            if askingContinuations.contains(where: rest.hasPrefix) {
                return true
            }
        }

        // Only the part before a question put to somebody else is read for
        // question words: "明天问老师作业怎么交" asks the teacher, not the
        // assistant.
        let asked = partAskedOfTheAssistant(opening)
        if containsQuestionWord(asked) || hasAOrNotA(asked) {
            return true
        }
        return nil
    }

    /// Han characters and no kana. Japanese shares the characters for "today"
    /// and "tomorrow" and asks its questions differently, so a Japanese
    /// sentence is left to the general rules rather than misread as Chinese.
    private static func isChinese(_ text: String) -> Bool {
        text.unicodeScalars.contains(where: isHan)
            && !text.unicodeScalars.contains(where: isKana)
    }

    private static func partAskedOfTheAssistant(_ opening: Substring) -> Substring {
        let positions = embeddedAskingVerbs.compactMap { verb -> String.Index? in
            guard let range = opening.range(of: verb),
                  range.lowerBound > opening.startIndex else {
                return nil
            }
            return range.lowerBound
        }
        guard let first = positions.min() else {
            return opening
        }
        return opening[..<first]
    }

    private static func containsQuestionWord(_ text: Substring) -> Bool {
        for word in questionWords {
            var searchFrom = text.startIndex
            while searchFrom < text.endIndex,
                  let range = text.range(of: word, range: searchFrom..<text.endIndex) {
                let before = text[..<range.lowerBound]
                if fewMeasures.contains(word),
                   fewVerbs.contains(where: before.hasSuffix) {
                    searchFrom = range.upperBound
                    continue
                }
                return true
            }
        }
        return false
    }

    /// 交不交, 来不来得及, 需不需要, 交了没交: the same character either side
    /// of 不 or 没 is the commonest way to ask a yes-or-no question, and it
    /// never appears in a list of question words because it can be made out
    /// of any verb.
    private static func hasAOrNotA(_ text: Substring) -> Bool {
        let characters = Array(text)
        guard characters.count >= 3 else {
            return false
        }
        for index in 1..<(characters.count - 1)
        where "不没沒".contains(characters[index]) {
            let before = characters[index - 1]
            if before == characters[index + 1],
               before.unicodeScalars.allSatisfy(isHan) {
                return true
            }
        }
        return false
    }

    static func namesATime(_ text: String) -> Bool {
        guard !text.unicodeScalars.contains(where: isKana) else {
            return false
        }
        // 一共, 一直, 一起: the 一 there is not Monday's. Read with it, "这周
        // 一共要交三个作业" named a day it does not mention.
        var text = text
        for phrase in ["一共", "一直", "一起", "一般", "一定", "一些"] {
            text = text.replacingOccurrences(
                of: phrase,
                with: "＿" + phrase.dropFirst()
            )
        }
        if timeWords.contains(where: text.contains) {
            return true
        }

        let characters = Array(text)
        for index in characters.indices.dropFirst() {
            let mark = characters[index]
            let isClock = clockMarks.contains(mark)
            let isDate = dateMarks.contains(mark)
            let isHour = mark == "时" || mark == "時"
            guard isClock || isDate || isHour,
                  numerals.contains(characters[index - 1]) else {
                continue
            }

            // The run of numerals the mark follows: 十二 in 十二点.
            var start = index - 1
            while start > 0, numerals.contains(characters[start - 1]) {
                start -= 1
            }
            // 第三点 is the third point, not three o'clock.
            if start > 0, characters[start - 1] == "第" {
                continue
            }
            let isLoneOne = index - start == 1 && characters[start] == "一"

            if isHour {
                // 15时 is a time; 一时 and 同时 are not.
                if asciiDigits.contains(characters[index - 1]) {
                    return true
                }
                continue
            }
            if isDate {
                // 一日 is "one day" far more often than the first.
                if mark == "日", isLoneOne {
                    continue
                }
                return true
            }
            // 一点 means "a little" far more often than one o'clock, so on its
            // own it does not count: it has to be said after a part of the
            // day, or carry on the way a clock time does.
            if isLoneOne {
                let next = index + 1 < characters.count
                    ? characters[index + 1]
                    : nil
                let carriesOn = next.map {
                    afterAClockTime.contains($0) || numerals.contains($0)
                } ?? false
                let before = String(characters[..<start])
                guard carriesOn || dayParts.contains(where: before.hasSuffix) else {
                    continue
                }
            }
            return true
        }
        return false
    }

    private static func isKana(_ scalar: Unicode.Scalar) -> Bool {
        (0x3040...0x30FF).contains(scalar.value)
    }

    private static func isHan(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x3400...0x4DBF, 0x4E00...0x9FFF, 0xF900...0xFAFF,
             0x20000...0x2FA1F:
            true
        default:
            false
        }
    }
}

/// Where the list and the panel sit in the column they share.
struct AssistantPaneLayout: Equatable, Sendable {
    /// The width the assignments are laid out at.
    ///
    /// Still the whole column while the panel covers them: they stay as they
    /// were underneath rather than being squeezed to nothing, so closing the
    /// panel puts back exactly what was there, scrolled to where it was.
    let listWidth: CGFloat
    /// The panel and its divider together. Zero when the panel is closed.
    let panelWidth: CGFloat
    /// The column is too narrow for both, so the panel has all of it.
    let panelCoversList: Bool
}

/// How much room the assistant panel takes beside the assignments.
enum AssistantLayout {
    static let sidebarMinimum: CGFloat = 300
    static let sidebarIdeal: CGFloat = 380
    static let sidebarMaximum: CGFloat = 440

    /// What the assignment list keeps when the panel is open. Below this the
    /// titles are unreadable, so the panel gives width back instead of taking
    /// more.
    static let mainContentFloor: CGFloat = 300

    /// The narrowest either pane is ever laid out at.
    ///
    /// Below this the panel's own controls no longer fit across it and the
    /// list's title no longer fits on a line. Text that is given less does not
    /// get smaller, it wraps a letter at a time, and a row of buttons that is
    /// given less does not shrink at all: it spills out over whatever is
    /// beside it. So nothing is ever asked to be narrower than this.
    static let paneMinimum: CGFloat = 280

    /// The room the list and the panel need to sit beside each other. Below it
    /// something has to give, and the navigation sidebar is the cheapest thing
    /// to fold away: it is one click to bring back, and unlike the window's own
    /// width it is the app's to decide.
    static var minimumWidthForBoth: CGFloat {
        mainContentFloor + sidebarMinimum
    }

    /// The least a column can be and still show both. Narrower than this and
    /// halving it would hand each pane less than it can be read at, so the
    /// panel takes the column and the list waits underneath.
    static var minimumWidthToShare: CGFloat {
        paneMinimum * 2
    }

    /// Where the two panes go in a column of the given width.
    ///
    /// The panel is never taken away for want of room, which is the rule the
    /// rest of this type exists to keep. What changes with the width is only
    /// how the column is divided: beside the list while both can be read, and
    /// over it when they cannot.
    static func panes(
        isPanelShown: Bool,
        preferred: CGFloat?,
        columnWidth: CGFloat,
        dividerWidth: CGFloat = 1
    ) -> AssistantPaneLayout {
        let column = columnWidth.isFinite ? max(0, columnWidth) : 0
        guard isPanelShown else {
            return AssistantPaneLayout(
                listWidth: column,
                panelWidth: 0,
                panelCoversList: false
            )
        }
        guard column >= minimumWidthToShare else {
            return AssistantPaneLayout(
                listWidth: column,
                panelWidth: column,
                panelCoversList: true
            )
        }
        let panel = min(
            column,
            fittedSidebarWidth(preferred: preferred, availableWidth: column)
                + max(0, dividerWidth)
        )
        return AssistantPaneLayout(
            listWidth: column - panel,
            panelWidth: panel,
            panelCoversList: false
        )
    }

    /// The presentation to use, given what the user asked for and what is
    /// already on screen.
    ///
    /// The window's width is not a parameter: it cannot take a container away.
    static func presentation(
        preference: AssistantPresentationPreference,
        current: ActiveAssistantPresentation
    ) -> ActiveAssistantPresentation {
        // A detached window is only ever chosen explicitly, so nothing here
        // takes it away.
        guard current != .separateWindow else {
            return .separateWindow
        }

        switch preference {
        case .sidebar:
            return .sidebar
        case .popover:
            return .popover
        }
    }

    /// The width to actually draw the panel at, which is the preferred width
    /// trimmed to what the window already has.
    ///
    /// This is what keeps a split-screen window the size the user left it: the
    /// panel is only ever as wide as the space beside the list, so opening it
    /// never asks macOS for a wider window. A column a little short of both
    /// floors is halved; one too narrow for two readable halves goes to the
    /// panel whole.
    static func fittedSidebarWidth(
        preferred: CGFloat?,
        availableWidth: CGFloat
    ) -> CGFloat {
        let wanted = clampedSidebarWidth(preferred)
        guard availableWidth.isFinite, availableWidth > 0 else {
            return wanted
        }
        guard availableWidth >= minimumWidthToShare else {
            return availableWidth
        }
        let roomBesideTheList = availableWidth - mainContentFloor
        guard roomBesideTheList >= sidebarMinimum else {
            return min(wanted, availableWidth / 2)
        }
        return min(wanted, roomBesideTheList)
    }

    /// Keeps a stored width inside the usable range, so a bad value from
    /// preferences cannot produce an unusable panel.
    static func clampedSidebarWidth(_ width: CGFloat?) -> CGFloat {
        guard let width, width.isFinite, width > 0 else {
            return sidebarIdeal
        }
        return min(max(width, sidebarMinimum), sidebarMaximum)
    }

    static func isValidSidebarWidth(_ width: CGFloat) -> Bool {
        width.isFinite && width >= sidebarMinimum && width <= sidebarMaximum
    }
}

/// The assignment an answer is about, kept short enough for a chip.
struct AssistantContext: Equatable, Sendable, Identifiable {
    let id: UUID
    let title: String
    let courseName: String?

    init(_ item: AssignmentListItem) {
        id = item.id
        title = item.title
        courseName = item.normalizedCourseName
    }

    /// Truncated for display. A full Canvas course title would stretch the chip
    /// across the panel.
    var chipTitle: String {
        title.count <= 32 ? title : title.prefix(31).trimmingCharacters(
            in: .whitespacesAndNewlines
        ) + "…"
    }
}
