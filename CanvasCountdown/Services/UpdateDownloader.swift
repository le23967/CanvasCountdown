import AppKit
import Foundation

protocol UpdateDownloading: Sendable {
    /// Fetches the disk image and leaves it somewhere the person can find it,
    /// returning where it went.
    func download(_ release: AppRelease) async throws -> URL

    /// Opens the downloaded image, so the volume with the app in it appears.
    @MainActor
    func reveal(_ file: URL)
}

enum UpdateDownloadError: LocalizedError, Equatable, Sendable {
    case noDiskImage
    case httpStatus(Int)
    case networkFailure(code: Int?)
    case couldNotSave

    var errorDescription: String? {
        switch self {
        case .noDiskImage:
            "That release does not have a disk image to download."
        case let .httpStatus(status):
            "The download failed (HTTP \(status))."
        case .networkFailure:
            "The download could not be completed."
        case .couldNotSave:
            "The download could not be saved to your Downloads folder."
        }
    }
}

/// Downloads the new version into Downloads and opens it.
///
/// It stops there on purpose. This app is sandboxed and ad-hoc signed: it has
/// no write access to `/Applications` and no signing identity that would let a
/// replacement prove it is the same app. Swapping itself out is therefore not
/// something it can do honestly, and pretending otherwise would mean an update
/// that half-works and an app that will not reopen. So the last step is a drag,
/// the same one the first install asked for.
struct DownloadsFolderUpdateDownloader: UpdateDownloading {
    private let session: URLSession

    init(session: URLSession = .shared) {
        self.session = session
    }

    func download(_ release: AppRelease) async throws -> URL {
        guard let source = release.downloadURL else {
            throw UpdateDownloadError.noDiskImage
        }

        let temporary: URL
        let response: URLResponse
        do {
            (temporary, response) = try await session.download(from: source)
        } catch let error as URLError {
            throw UpdateDownloadError.networkFailure(code: error.errorCode)
        } catch {
            throw UpdateDownloadError.networkFailure(code: nil)
        }

        if let http = response as? HTTPURLResponse, http.statusCode != 200 {
            throw UpdateDownloadError.httpStatus(http.statusCode)
        }

        let destination = try Self.destination(
            named: source.lastPathComponent
        )
        do {
            try FileManager.default.moveItem(at: temporary, to: destination)
        } catch {
            throw UpdateDownloadError.couldNotSave
        }
        return destination
    }

    @MainActor
    func reveal(_ file: URL) {
        // Opening the image hands it to DiskImageMounter, which mounts the
        // volume and shows the app beside the Applications shortcut — the same
        // window the first install came through.
        NSWorkspace.shared.open(file)
    }

    /// A name that does not overwrite a file already sitting in Downloads.
    /// Somebody who downloaded this by hand an hour ago should not have that
    /// replaced without being asked.
    private static func destination(named name: String) throws -> URL {
        let manager = FileManager.default
        guard let downloads = try? manager.url(
            for: .downloadsDirectory,
            in: .userDomainMask,
            appropriateFor: nil,
            create: true
        ) else {
            throw UpdateDownloadError.couldNotSave
        }

        let candidate = downloads.appendingPathComponent(name)
        guard manager.fileExists(atPath: candidate.path) else {
            return candidate
        }

        let base = candidate.deletingPathExtension().lastPathComponent
        let ext = candidate.pathExtension
        for suffix in 2...99 {
            let next = downloads
                .appendingPathComponent("\(base) \(suffix)")
                .appendingPathExtension(ext)
            if !manager.fileExists(atPath: next.path) {
                return next
            }
        }
        throw UpdateDownloadError.couldNotSave
    }
}

/// Writes nothing and opens nothing.
struct InertUpdateDownloader: UpdateDownloading {
    func download(_ release: AppRelease) async throws -> URL {
        throw UpdateDownloadError.noDiskImage
    }

    @MainActor
    func reveal(_ file: URL) {}
}
