import Foundation
import Security

protocol FeedURLStoring: Sendable {
    func saveFeedURL(_ url: URL) async throws
    func loadFeedURL() async throws -> URL?
    func deleteFeedURL() async throws
}

enum FeedURLValidationError: LocalizedError, Equatable, Sendable {
    case HTTPSRequired
    case missingHost
    case embeddedCredentialsNotAllowed

    var errorDescription: String? {
        switch self {
        case .HTTPSRequired:
            "The Canvas calendar address must use HTTPS."
        case .missingHost:
            "Enter a complete Canvas calendar address."
        case .embeddedCredentialsNotAllowed:
            "The calendar address must not contain embedded sign-in credentials."
        }
    }
}

enum KeychainFeedURLStoreError: LocalizedError, Equatable, Sendable {
    case textEncodingFailed
    case storedValueIsInvalid
    case operationFailed(status: OSStatus)

    var errorDescription: String? {
        switch self {
        case .textEncodingFailed:
            "The calendar address could not be secured."
        case .storedValueIsInvalid:
            "The saved calendar address is invalid. Enter it again."
        case .operationFailed:
            "The calendar address could not be accessed in Keychain."
        }
    }

    /// Suitable for local diagnostics. It contains an OSStatus only, never the
    /// Keychain value, account query, or private feed address.
    var diagnosticCode: String {
        switch self {
        case .textEncodingFailed:
            "keychain.text-encoding"
        case .storedValueIsInvalid:
            "keychain.invalid-stored-value"
        case let .operationFailed(status):
            "keychain.osstatus.\(status)"
        }
    }
}

enum FeedURLValidator {
    static func validated(_ url: URL) throws -> URL {
        guard url.scheme?.lowercased() == "https" else {
            throw FeedURLValidationError.HTTPSRequired
        }
        guard let host = url.host, !host.isEmpty else {
            throw FeedURLValidationError.missingHost
        }
        guard url.user == nil, url.password == nil else {
            throw FeedURLValidationError.embeddedCredentialsNotAllowed
        }
        return url
    }
}

actor KeychainFeedURLStore: FeedURLStoring {
    private let service: String
    private let account: String

    init(
        service: String = "com.local.CanvasCountdown",
        account: String = "canvas-calendar-feed"
    ) {
        self.service = service
        self.account = account
    }

    func saveFeedURL(_ url: URL) async throws {
        let secureURL = try FeedURLValidator.validated(url)
        guard let data = secureURL.absoluteString.data(using: .utf8) else {
            throw KeychainFeedURLStoreError.textEncodingFailed
        }

        let query = baseQuery
        let attributes: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String:
                kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        let updateStatus = SecItemUpdate(
            query as CFDictionary,
            attributes as CFDictionary
        )

        switch updateStatus {
        case errSecSuccess:
            return
        case errSecItemNotFound:
            var item = query
            for (key, value) in attributes {
                item[key] = value
            }
            let addStatus = SecItemAdd(item as CFDictionary, nil)
            guard addStatus == errSecSuccess else {
                throw KeychainFeedURLStoreError.operationFailed(
                    status: addStatus
                )
            }
        default:
            throw KeychainFeedURLStoreError.operationFailed(
                status: updateStatus
            )
        }
    }

    func loadFeedURL() async throws -> URL? {
        var query = baseQuery
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne

        var result: CFTypeRef?
        let status = SecItemCopyMatching(
            query as CFDictionary,
            &result
        )

        if status == errSecItemNotFound {
            return nil
        }
        guard status == errSecSuccess else {
            throw KeychainFeedURLStoreError.operationFailed(status: status)
        }
        guard
            let data = result as? Data,
            let value = String(data: data, encoding: .utf8),
            let url = URL(string: value)
        else {
            throw KeychainFeedURLStoreError.storedValueIsInvalid
        }

        do {
            return try FeedURLValidator.validated(url)
        } catch {
            throw KeychainFeedURLStoreError.storedValueIsInvalid
        }
    }

    func deleteFeedURL() async throws {
        let status = SecItemDelete(baseQuery as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw KeychainFeedURLStoreError.operationFailed(status: status)
        }
    }

    private var baseQuery: [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecAttrSynchronizable as String: kCFBooleanFalse as Any,
        ]
    }
}
