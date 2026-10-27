# Sketch

A freehand sketching app.

## Acceptance checklist

1. An `InkCanvas` filling most of the window, accepting pen, mouse and touch input.
2. A toolbar above the canvas with: pen color choice (at least black, red, blue, green), pen size choice (small, medium, large), an eraser toggle, Undo (removes the last stroke) and Clear (removes all strokes).
3. Clear asks for confirmation before removing all strokes.
4. A stroke counter in the toolbar or a status bar that updates as strokes are added or removed.
5. A "Save" button that saves the drawing as a GIF or PNG with a file save picker (initialized with the window handle).
6. The toolbar uses symbol icons with tooltips for every button.
7. A light background behind the canvas regardless of the app theme.
8. ViewModel with CommunityToolkit.Mvvm for the toolbar state (color, size, eraser, stroke count); InkCanvas API calls may be in code-behind.

<!-- controls: InkCanvas; ToggleButton|AppBarToggleButton|ToggleSwitch|CheckBox; ComboBox|DropDownButton|SplitButton|RadioButtons|Flyout|MenuFlyout -->
