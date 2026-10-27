# Timesheet

A personal timesheet for logging work hours. Data lives in memory (seed six entries in the current week).

## Acceptance checklist

1. A "Timesheet" screen with an entry form at the top: Date (date picker), Project (combo box of four project names), Hours (number box, 0.25 to 12, step 0.25), Billable (check box) and Description (text).
2. An "Add entry" button that is enabled only when a date and a project are chosen and Hours is greater than 0.
3. A table of entries below the form: Date, Project, Hours (one decimal), Billable, Description.
4. A week selector: "From" and "To" date pickers that limit the table to entries in that range.
5. A summary card showing total hours, billable hours and the billable percentage for the visible entries.
6. Each row has a "Remove" action.
7. An error banner when the user tries to add an entry that would put that day above 12 hours (the entry is not added).
8. MVVM with CommunityToolkit.Mvvm.

<!-- controls: CalendarDatePicker|DatePicker; ComboBox; NumberBox; CheckBox; ListView|GridView|ItemsControl|ItemsRepeater; InfoBar -->
