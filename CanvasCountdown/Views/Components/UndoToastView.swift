import SwiftUI

/// A small floating card offering to take back what was just added.
///
/// It appears for both the assistant and the ordinary editor, because adding
/// the wrong thing by hand is the same mistake either way. It counts itself
/// down and leaves, so it is never sitting in the way — and the Edit menu keeps
/// the same undo afterwards, which is what makes this safe to build on without
/// assuming anyone knows a keyboard shortcut.
struct UndoToastView: View {
    let message: String
    let secondsRemaining: Int
    let totalSeconds: Int
    let onUndo: () -> Void
    let onDismiss: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: "checkmark.circle.fill")
                .foregroundStyle(.green)
                .accessibilityHidden(true)

            Text(message)
                .lineLimit(1)

            countdown

            Button("Undo", action: onUndo)
                .buttonStyle(.borderedProminent)
                .controlSize(.small)
                .help("Remove what was just added")

            Button(action: onDismiss) {
                Image(systemName: "xmark")
                    .font(.caption)
            }
            .buttonStyle(.plain)
            .foregroundStyle(.secondary)
            .help("Keep it")
            .accessibilityLabel("Keep it")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(.regularMaterial, in: Capsule())
        .overlay {
            Capsule().strokeBorder(Color.primary.opacity(0.10), lineWidth: 1)
        }
        .shadow(color: .black.opacity(0.22), radius: 14, y: 5)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("\(message). Undo available for \(secondsRemaining) more seconds.")
    }

    /// The seconds left, drawn as a ring that empties. Shown as a number too:
    /// a ring alone says something is running out without saying what or when.
    private var countdown: some View {
        ZStack {
            Circle()
                .stroke(Color.secondary.opacity(0.25), lineWidth: 2)
            Circle()
                .trim(from: 0, to: fraction)
                .stroke(
                    Color.accentColor,
                    style: StrokeStyle(lineWidth: 2, lineCap: .round)
                )
                .rotationEffect(.degrees(-90))
            Text("\(secondsRemaining)")
                .font(.caption2.weight(.semibold))
                .monospacedDigit()
        }
        .frame(width: 22, height: 22)
        .animation(.linear(duration: 0.25), value: secondsRemaining)
        .accessibilityHidden(true)
    }

    private var fraction: CGFloat {
        guard totalSeconds > 0 else {
            return 0
        }
        return CGFloat(secondsRemaining) / CGFloat(totalSeconds)
    }
}
