# Photo Browser

A photo gallery with a grid and a full-size viewer.

## Acceptance checklist

1. A "Gallery" screen with a `GridView` of photo thumbnails (use at least twelve https image URLs such as `https://picsum.photos/id/<n>/400/300`), each tile showing the image and its title underneath.
2. A search box that filters photos by title.
3. Clicking a tile opens a viewer showing that photo full size in a `FlipView`, positioned on the clicked photo; flipping moves through all photos.
4. The viewer has a "Back to gallery" button and shows the current photo's title and its index ("3 of 12").
5. Each photo has a "Favorite" toggle (in the viewer) and the gallery has a "Favorites only" toggle that filters the grid.
6. Thumbnails are uniform size (about 200 x 150) and wrap to the window width.
7. An empty-state text when the filter matches no photos.
8. MVVM with CommunityToolkit.Mvvm for the photo collection, filter and selected photo.

<!-- controls: GridView; FlipView; Image; TextBox|AutoSuggestBox; ToggleSwitch|ToggleButton|CheckBox -->
