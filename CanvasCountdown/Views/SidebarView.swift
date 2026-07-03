import SwiftUI

struct SidebarView: View {
    @Binding var selection: SidebarDestination?
    let upcomingCount: Int

    var body: some View {
        List(selection: $selection) {
            Section("Assignments") {
                ForEach(SidebarDestination.allCases.filter { $0 != .settings }) { destination in
                    Label {
                        HStack {
                            Text(destination.title)
                            Spacer()
                            if destination == .upcoming, upcomingCount > 0 {
                                Text("\(upcomingCount)")
                                    .foregroundStyle(.secondary)
                                    .monospacedDigit()
                            }
                        }
                    } icon: {
                        Image(systemName: destination.systemImage)
                    }
                    .tag(destination)
                }
            }

            Section {
                Label(SidebarDestination.settings.title, systemImage: SidebarDestination.settings.systemImage)
                    .tag(SidebarDestination.settings)
            }
        }
        .listStyle(.sidebar)
        .navigationTitle("Canvas Countdown")
    }
}
