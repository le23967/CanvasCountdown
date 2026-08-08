import SwiftUI

/// Says a newer version exists, and gets it.
///
/// A strip at the top of the window rather than a dialog in the middle of it.
/// Nothing here is urgent: a deadline app that interrupts you to talk about
/// itself has its priorities the wrong way round, and the one thing this must
/// never do is stand between somebody and the list they opened the app to read.
struct UpdateBannerView: View {
    let release: AppRelease
    let isDownloading: Bool
    /// What happened to the last download, if anything has been tried.
    let message: String?
    let onDownload: () -> Void
    let onReadMore: () -> Void
    let onDismiss: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: "arrow.down.circle.fill")
                .foregroundStyle(.tint)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 3) {
                Text("Version \(release.version.description) is available")
                    .font(.callout.weight(.semibold))

                if let message {
                    Text(message)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                } else if !release.summary.isEmpty {
                    Text(release.summary)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(2)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            // The notes are the one thing here that can be any length, so they
            // are the one thing that gives way. Without this a long first
            // paragraph claims the whole row and pushes Download off the end of
            // the window — a notice about an update with no way to get it.
            .layoutPriority(-1)

            Spacer(minLength: 8)

            if isDownloading {
                ProgressView()
                    .controlSize(.small)
                    .accessibilityLabel("Downloading the update")
            } else {
                Button("What's New") {
                    onReadMore()
                }
                .buttonStyle(.link)
                .font(.callout)

                Button("Download") {
                    onDownload()
                }
                .buttonStyle(.borderedProminent)
                .help("Download the disk image and open it")
            }

            Button {
                onDismiss()
            } label: {
                Image(systemName: "xmark")
            }
            .buttonStyle(.borderless)
            .help("Not now — ask again when there is a newer version")
            .accessibilityLabel("Dismiss this update")
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.bar)
        .overlay(alignment: .bottom) {
            Divider()
        }
    }
}
