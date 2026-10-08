# Branding

The name and the logo of the application, the files that hold them, and whose trademarks appear in the name.

## The name

The application is called AMG DIGA Archive. Write AMG and DIGA in capital letters wherever a person reads the name: in the window, in the installer, in messages and in documents.

Some technical names are shorter and stay as they are, because installed copies depend on them when they are updated:

| What | Name |
|---|---|
| Repository | `diga-archive` |
| Program | `Diga.exe` |
| Installation folder | `%LOCALAPPDATA%\Programs\DIGA` |
| Folder for settings and saved sign-ins | `%LOCALAPPDATA%\Diga` |
| Release files | `DIGA-<version>-…` |

## The logo

![AMG DIGA Archive logo: a turquoise film ribbon forming the letter D around a play symbol, on a dark navy square](../src/Diga.App/Assets/Diga.png)

The logo is a film ribbon bent into a rounded letter D around a play symbol. It is turquoise on a dark navy square, with an amber-to-coral fold on its right edge. It contains no lettering. Inside the window the mark is shown without the square, on the window's own background.

| File | Size | Use |
|---|---|---|
| `src/Diga.App/Assets/BrandMaster.png` | 1254 × 1254 px | The master image. Every other file is made from it. |
| `src/Diga.App/Assets/BrandMark.png` | 512 × 512 px, transparent background | Shown inside the application, beside its name above the menu. |
| `src/Diga.App/Assets/Diga.ico` | 16, 24, 32, 48, 64, 128 and 256 px | The Windows icon: the program file, the title bar, the installer and the shortcuts. |
| `src/Diga.App/Assets/Diga.png` | 256 × 256 px | Installed with the application, though its code does not use the file at present. Shown at the top of the README and on this page. |
| `docs/branding/entra-app-logo-215.png` | 215 × 215 px, no transparency | Made for the Microsoft application registration that stands behind the built-in OneDrive sign-in (in Microsoft Entra: Branding & properties). |

The master image was made with an image-generation tool and has not been repainted since. `BrandMark.png` is the master without its navy background, so that the mark does not stand on a square whose colour differs from the window's. The script takes the colour of the master's four corners for the background and makes it transparent wherever it appears, also where the ribbon encloses it, and it smooths the edge between the two. The ribbon itself stays as it is, its dark faces included. Only the darkest end of a shadow, where it can hardly be told from the background's colour, fades out, so that it does not end in a step on a background of another colour. The other three files are conversions of the master in size and format, nothing more, and keep the square.

The size of the last file is Microsoft's requirement, not a choice of this project. Microsoft's documentation asks for a logo of exactly 215 × 215 pixels, in PNG format, of at most 100 KB, with a solid background and no transparency: [Properties of an enterprise application, Logo](https://learn.microsoft.com/entra/identity/enterprise-apps/application-properties#logo).

### Changing the logo

Replace `BrandMaster.png` with a square image and run:

```powershell
./scripts/Build-BrandAssets.ps1
```

The script writes the other four files again. It uses the imaging functions of Windows, so it runs on Windows only. It stops if the master is not square, if the four corners of the master are not one flat colour (that colour is what the mark for the window loses), or if the 215-pixel logo would be larger than 100 KB.

## Trademarks

Panasonic and DIGA are trademarks of their owner. AMG DIGA Archive is an independent project: it is not made, endorsed or supported by Panasonic. The logo uses no mark of Panasonic.

Microsoft, OneDrive, Google and Google Drive are trademarks of their owners.
