# Invoices

A small-business invoicing app. The main screen lists invoices; a second screen creates or edits one invoice.
Data lives in memory (seed five sample invoices at startup); no persistence or network is needed.

Navigation: a side navigation pane with two entries, "Invoices" and "New invoice".

## Acceptance checklist

1. An "Invoices" screen with a table of invoices showing columns Number, Client, Issue date, Due date, Amount (formatted as currency) and Status.
2. A search box above the table that filters invoices by client name as the user types.
3. A total of the outstanding (status not "Paid") amounts, shown above the table and formatted as currency.
4. Each row has a "Mark paid" action that sets that invoice's status to "Paid" and updates the outstanding total.
5. A "New invoice" screen with a form: Client (text), Issue date and Due date (date pickers), Amount (number, minimum 0), Status (choice of Draft, Sent, Paid) and Notes (multi-line text).
6. A "Save" button on the form, enabled only when Client is not empty and Amount is greater than 0; saving adds the invoice to the list and shows a short success notification.
7. An empty-state message in the table when no invoice matches the search.
8. MVVM: every screen has a ViewModel built on CommunityToolkit.Mvvm; controls bind to ViewModel properties and buttons to commands.

<!-- controls: NavigationView; ListView|GridView|ItemsControl|ItemsRepeater; TextBox; CalendarDatePicker|DatePicker; NumberBox; ComboBox|RadioButtons; InfoBar|TeachingTip -->
