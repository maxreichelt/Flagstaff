# Ticket Tracker

A support ticket tracker. Data lives in memory (seed ten tickets with mixed priorities and statuses).

Navigation: a top navigation bar with "Tickets" and "Reports".

## Acceptance checklist

1. A "Tickets" screen with a table: Id, Title, Priority, Status, Assignee, Created (date).
2. Filters above the table: a Status combo box (All, Open, In progress, Closed), a Priority combo box (All, Low, Medium, High) and a search box on the title; changing any filter updates the table.
3. A "New ticket" button that opens a dialog with Title, Priority and Assignee fields and Cancel/Create buttons; Create adds the ticket.
4. Each row has a "Delete" action that first asks for confirmation ("Delete ticket?" with a danger-styled Delete button and a Cancel button); only on confirmation is the ticket removed.
5. Each row has a "Close" action, disabled when the ticket is already closed.
6. A count of visible tickets ("12 tickets") above the table.
7. A "Reports" screen with three KPI cards: open tickets, high-priority open tickets, tickets closed this week.
8. A success notification after a ticket is created or deleted.
9. MVVM with CommunityToolkit.Mvvm.

<!-- controls: NavigationView; ListView|GridView|ItemsControl|ItemsRepeater; ComboBox; TextBox; ContentDialog|Grid; InfoBar|TeachingTip -->
