# UI Styling - AUTOMATED! 🎨

## What Changed

The Lorcana Deck Builder UI has been completely redesigned with:

✅ **Tailwind CSS v4** - Latest version with modern features
✅ **Automated build** - CSS builds automatically when you run `aspire run`
✅ **Beautiful design** - Purple/slate gradients, glassmorphism, authentic Lorcana ink colors
✅ **Zero manual steps** - No need to remember npm commands!

## How It Works

### The Magic MSBuild Target

Added to `DeckBuilder.Ui.fsproj`:

```xml
<Target Name="BuildTailwindCSS" BeforeTargets="BeforeBuild">
  <Message Text="Building Tailwind CSS..." Importance="high" />
  <Exec Command="npm install" WorkingDirectory="wwwroot" Condition="!Exists('wwwroot\node_modules')" />
  <Exec Command="npm run build:css" WorkingDirectory="wwwroot" />
</Target>
```

### Tailwind v4 Content Scanning

In `input.css`, we use `@source` directives to tell Tailwind where to find classes:

```css
@import "tailwindcss";

@source "../**/*.fs";
@source "*.html";

@theme {
  --color-lorcana-amber: #FDB731;
  --color-lorcana-amethyst: #9966CC;
  --color-lorcana-emerald: #22C55E;
  --color-lorcana-ruby: #DC2626;
  --color-lorcana-sapphire: #3B82F6;
  --color-lorcana-steel: #71717A;
}
```

This ensures Tailwind scans all F# files for class names and includes them in the build.

### Files Automatically Handled

- `input.css` → Tailwind source with Lorcana theme and @source directives
- `styles.css` → Auto-generated (~23KB), auto-copied to output
- `node_modules/` → Auto-installed when needed

## Just Run It! 🚀

```bash
aspire run
```

That's it! Everything else is automatic:
- ✅ npm dependencies installed
- ✅ CSS compiled with Tailwind (scans F# files!)
- ✅ All classes from F# code included
- ✅ Styles copied to wwwroot
- ✅ Application served with beautiful UI

## The Beautiful UI Features

### 🎨 Design Elements
- **Gradient background**: Purple/slate for magical Lorcana aesthetic
- **Glassmorphism cards**: Frosted glass effect with backdrop blur
- **Smooth animations**: Hover effects, transitions, loading states
- **Responsive layout**: Mobile and desktop ready

### 🎴 Lorcana Ink Colors
All six ink types with authentic colors:
- ⚡ **Amber** - Bright gold (#FDB731)
- 💎 **Amethyst** - Rich purple (#9966CC)  
- 🌿 **Emerald** - Vibrant green (#22C55E)
- 🔥 **Ruby** - Deep red (#DC2626)
- 💧 **Sapphire** - Bright blue (#3B82F6)
- ⚔️ **Steel** - Cool gray (#71717A)

### 📱 UI Components
- **Form inputs**: Styled select boxes and text areas with focus states
- **Build button**: Gradient button with hover animation
- **Card list**: Beautiful deck display with circular count badges
- **Status badges**: Inkable indicators and color-coded tags
- **Loading state**: Animated spinner matching the theme

## Development

### Modify the UI
1. Edit `DeckBuilder.Ui/Program.fs` for F# component changes
2. Edit `DeckBuilder.Ui/wwwroot/input.css` for custom CSS/theme
3. Run `aspire run` - everything rebuilds automatically!

### Watch Mode (Optional)
If you want live CSS reloading during development:

```bash
cd DeckBuilder.Ui/wwwroot
npm run watch:css
```

But you don't need to - the automated build works fine!

## Troubleshooting

### Classes not appearing?
The CSS file should be ~23KB. If it's tiny:
1. Check that `@source` directives are in `input.css`
2. Run `npm run build:css` manually in `wwwroot/` to test
3. Verify `styles.css` is copied to `bin/Debug/net8.0/wwwroot/`

### Still not working?
1. Hard refresh browser (Ctrl+F5)
2. Check browser console for 404 on styles.css
3. Verify `<link rel="stylesheet" href="styles.css"/>` is in index.html

## No More "Ugly as Sin" 😎

The UI went from basic HTML forms to a premium, modern interface worthy of Disney Lorcana. 

**Just run `aspire run` and enjoy!**
