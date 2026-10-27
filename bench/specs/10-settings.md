# Settings

An app settings screen that adapts to the window width.

## Acceptance checklist

1. A "Settings" screen with three sections: Appearance (theme choice Light/Dark/System as radio buttons, an accent color combo box), Notifications (toggle switches for Email, Push and Weekly digest) and Account (display name text box, email text box, a "Sign out" button).
2. Choosing a theme applies it to the window content immediately (set `RequestedTheme` on the root element).
3. Adaptive layout with `VisualStateManager` and `AdaptiveTrigger`: at window widths of 800 px or more the three sections are shown in two columns; below 800 px they stack in one column.
4. In the narrow state, the section headers use a smaller font size than in the wide state.
5. An "Advanced" `Expander` (collapsed by default) holding a "Developer mode" toggle and a "Cache size" slider (64 to 2048 MB) with its value shown next to it.
6. A "Reset to defaults" button that asks for confirmation and then restores all settings.
7. A "Saved" notification after any setting changes.
8. MVVM with CommunityToolkit.Mvvm for every setting.

<!-- controls: VisualStateManager; AdaptiveTrigger; RadioButton|RadioButtons; ToggleSwitch; Expander; Slider; ComboBox -->
