# Inventory Dashboard

A warehouse inventory dashboard. Data lives in memory (seed twelve products across three categories).

## Acceptance checklist

1. A "Dashboard" screen with four KPI cards in a row: Total products, Total stock value (currency), Low-stock items (count, highlighted as a warning) and Out-of-stock items (count, highlighted as danger).
2. Below the cards, a table of products with columns SKU, Name, Category, Quantity, Unit price (currency) and Value (currency).
3. A category filter (combo box with "All" plus the three categories) above the table that filters the rows and updates the KPI cards.
4. A "Show low stock only" toggle that limits the table to products with quantity below 10.
5. A "Refresh" button in a toolbar at the top that reloads the data.
6. A progress bar or ring shown while the data is loading.
7. A warning banner at the top when any product is out of stock, with a button that turns on the low-stock filter.
8. MVVM with CommunityToolkit.Mvvm.

<!-- controls: ListView|GridView|ItemsControl|ItemsRepeater; ComboBox; ToggleSwitch|CheckBox; ProgressBar|ProgressRing; InfoBar -->
