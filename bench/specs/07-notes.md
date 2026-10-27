# Notes

A tabbed notes editor.

## Acceptance checklist

1. A `MenuBar` at the top with menus File (New note, Close note, Exit), Edit (Clear) and Help (About).
2. A `TabView` below the menu bar; each tab is one note, titled with the note's title; the add-tab button creates a new note and tabs can be closed.
3. Each tab contains a Title text box and a multi-line Body text box that fills the available space.
4. Each tab has an `Expander` titled "Details" (collapsed by default) holding a Tags text box, a "Pinned" toggle and a read-only "Last edited" text.
5. Start with two sample notes open.
6. "New note" in the File menu does the same as the add-tab button; "Close note" closes the selected tab.
7. "About" opens a dialog with the app name and an OK button.
8. A status line at the bottom showing the number of open notes and the word count of the selected note.
9. MVVM with CommunityToolkit.Mvvm for the notes collection and the selected note.

<!-- controls: MenuBar; MenuFlyoutItem; TabView; Expander; TextBox; ToggleSwitch|CheckBox -->
