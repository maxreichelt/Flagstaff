# CRM Contacts

A contact manager for a small sales team, built as a master-detail screen. Data lives in memory (seed eight
sample contacts at startup).

## Acceptance checklist

1. A single "Contacts" screen split into a master list on the left (fixed width about 320 px) and a detail pane on the right that fills the remaining space.
2. The master list shows each contact's name (prominent) and company (secondary), plus a "VIP" badge only for VIP contacts.
3. Selecting a contact shows its details on the right: editable Name, Email, Phone, Company, a "VIP" toggle and a multi-line Notes field.
4. When no contact is selected, the detail pane shows the text "Select a contact." instead of the form.
5. A "Save" button in the detail pane writes the edits back to the selected contact and refreshes it in the list.
6. An "Add contact" button in a toolbar at the top creates a new empty contact, adds it to the list and selects it.
7. A "Delete" button in the detail pane removes the selected contact after a confirmation dialog ("Delete contact?").
8. A search box in the toolbar filters the master list by name or company.
9. MVVM with CommunityToolkit.Mvvm; no logic in code-behind other than wiring.

<!-- controls: ListView|ItemsControl|ItemsRepeater; TextBox; ToggleSwitch|CheckBox; ContentDialog|Grid; Button -->
