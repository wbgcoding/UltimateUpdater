# Bundled brand icons

`src/UltimateUpdater/Services/BrandIconData.g.cs` contains monochrome brand marks taken from
[Simple Icons](https://github.com/simple-icons/simple-icons), which publishes them under
**CC0 1.0 Universal** (public domain dedication). Only the path data is embedded; no image files
are shipped, and nothing is downloaded at runtime.

Regenerate after changing the curated list in the script:

```
git clone --depth 1 --filter=blob:none --sparse https://github.com/simple-icons/simple-icons.git
python tools/make-brand-icons.py --icons simple-icons/icons --out src/UltimateUpdater/Services/BrandIconData.g.cs
```

## Trademarks

The CC0 dedication covers the icon files. It does **not** transfer any rights in the trademarks the
icons depict; those remain the property of their respective owners. They are used here for one
purpose only: to identify, in a list of available updates, the software the row refers to. No
affiliation, sponsorship or endorsement is claimed, and no mark is used as this application's own
identity; the app icon is its own design.

Some well-known marks are deliberately missing (Microsoft's among them). Their owners asked for
removal from the upstream set, so they are not bundled here either; those rows fall back to a
category symbol.

If you are the owner of a mark used here and would rather it were not, remove its slug from the list
in `tools/make-brand-icons.py` and regenerate; the application works unchanged without it.
