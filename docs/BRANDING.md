# AMG DIGA Archive identity

The visible application name is **AMG DIGA Archive**. Always capitalize **AMG** and **DIGA** in the title bar, wordmark, accessible name, messages, About information and installer labels. Repository URLs continue to use `AmgDigaArchive`. Preserve existing executable and settings identities for compatible upgrades. The original generation prompts below are historical records.

The identity connects the work of preserving recorded video with an immediately recognizable play symbol. A film ribbon forms a rounded **D**; a warm folded edge gives the mark a distinct silhouette. The wordmark remains real UI text, so it stays sharp and accessible at every display scale.

## Production assets

- `src/Diga.App/Assets/BrandMaster.png`: original generated master, preserved without repainting.
- `src/Diga.App/Assets/BrandMark.png`: 512 px in-app brand tile.
- `src/Diga.App/Assets/Diga.png`: 256 px application image.
- `src/Diga.App/Assets/Diga.ico`: 16, 24, 32, 48, 64, 128 and 256 px Windows icon frames.

The final artwork uses an opaque ink background for clean edges at small sizes. Teal/turquoise is the principal identity color; a limited amber/coral accent suggests the warmth of personal recordings. UI stage accents complement the logo, while native text, controls and high-contrast colors preserve readability. Color is never the only indication of stage or completion.

`scripts/Build-BrandAssets.ps1` performs only deterministic size/format conversion from the committed master using Windows imaging APIs. It does not regenerate or repaint the artwork. Run it after replacing the master. The app executable, title bar, installer, shortcuts and in-app branding use the same identity. Stable executable, installation and settings identities are retained for upgrades.

## Generation record

Artwork was created with the **built-in image-generation tool**, then exported to Windows icon formats. No API-key/CLI fallback was used. Transparent concept versions were not selected because their edge treatment was less suitable at small sizes. The final asset is the opaque icon edition.

Initial design prompt:

> Use case: logo-brand. Create the final production logo symbol for AmgDigaArchive, a premium Windows app that rescues original video recordings from Panasonic recorder disks and preserves them in a personal archive. Deliver ONE polished square app-logo mark, not a presentation sheet or mockup. Design: a distinctive geometric ribbon forming a rounded capital D / archival doorway, with an unmistakable small right-facing play triangle as clean negative space in the center; subtly suggest a film frame and a protected archive through the silhouette, without adding tiny pictograms. Precise, simple, balanced, thick forms that remain recognizable as a Windows taskbar icon. Contemporary professional software identity with character, no generic cloud or shield. Luminous teal and deep turquoise ribbon with a restrained warm amber/coral edge accent, very subtle dimensional layering but crisp vector-like edges and mostly flat fills. The symbol occupies about 82 percent of the square with generous even clear margins, centered and optically balanced. True transparent background outside the mark and inside the negative-space cutout. No square background tile, no shadow outside the symbol, no lettering, no words, no numbers, no watermark, no sample sizes, no extra variations. High resolution master artwork, suitable on both a deep ink/navy dark UI and a light ivory UI.

Final production prompt, applied to the refined concept:

> Create the final Windows application icon version of this exact logo, retaining the D film-ribbon and centered play motif. Place it on a perfectly smooth solid deep ink navy background, hex #101D2D, covering the complete square canvas. All open areas and film perforations are this same solid ink navy. Make the logo's edges completely crisp and vector-clean with no speckles, no ragged cutout edges, no floating pixels, no translucent noise. The icon mark uses luminous turquoise/teal with a restrained amber-to-coral folded ribbon accent on its right edge. Simplify to broad elegant surfaces and very subtle smooth gradients. One centered symbol occupying 76 percent of canvas width and height, generous even negative space. This is final high-resolution professional software identity artwork for AmgDigaArchive, polished and recognizable at 32 pixels. No text, no border, no frame around the square, no external shadow, no mockup, no extra logos. Opaque solid ink navy square background, no transparency.
