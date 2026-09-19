# P7M Attachment Converter for XrmToolBox

An [XrmToolBox](https://www.xrmtoolbox.com/) plugin that finds `.p7m`
(PKCS#7 / S-MIME signed) attachments on Microsoft Dynamics 365 /
Dataverse records and converts them back into their original,
readable file — without leaving the CRM data layer.

## The problem

Email systems that use S/MIME signing wrap outgoing attachments in a
PKCS#7 envelope, which renames a file like `invoice.pdf` to
`invoice.pdf.p7m`. When these emails are tracked or queued into
Dynamics 365 (via server-side sync, email routing, or manual
attachment), the `.p7m` wrapper comes along with them. The result is
an attachment CRM users can see but not open — Windows, Outlook, and
the CRM web UI have no native way to unwrap it, so the file just sits
there as an opaque blob.

This is a routine, recurring nuisance in GCC/Dynamics 365 environments
that route external, signed correspondence into case or account
records — someone eventually has to manually download the `.p7m`,
open it in a desktop mail client or third-party tool, extract the
original file, and re-upload it. This plugin automates that entire
round-trip from directly inside XrmToolBox.

## How it works

A `.p7m` file is a [PKCS#7 / CMS](https://en.wikipedia.org/wiki/PKCS_7)
envelope. The plugin:

1. Queries the `annotation` entity for any record whose `filename`
   ends in `.p7m`.
2. Decodes the PKCS#7 structure using .NET's built-in
   `System.Security.Cryptography.Pkcs` classes:
   - Tries `SignedCms` first (the common case — S/MIME *signed* mail).
   - Falls back to `EnvelopedCms` if the content is signed **and**
     encrypted, which requires a matching certificate/private key on
     the machine running XrmToolBox.
3. Extracts the original file bytes and its original filename
   (stripping the `.p7m` suffix).
4. Creates a **new** `annotation` on the same regarding record with
   the restored file, correct MIME type, and original name — the
   source `.p7m` annotation is left untouched, so nothing is deleted
   or overwritten.

## Features

- Bulk scan: finds every `.p7m` attachment across the organization
  (or scoped further with a FetchXML/query tweak).
- Checkbox selection — convert one, several, or all found attachments
  in a single batch.
- Per-row status column showing success or a specific failure reason,
  so a handful of encrypted/corrupt files don't block the rest of the
  batch.
- Non-destructive — original `.p7m` records are preserved.

## Installation

**Manual install (current):**
1. Download the latest build from the [Releases](../../releases) page
   (or build from source — see below).
2. Copy `Praveen.XrmToolbox.P7mAttachmentConverter.dll` into your
   XrmToolBox Plugins folder:
   `%AppData%\MscrmTools\XrmToolBox\Plugins`
3. Restart XrmToolBox. The tool appears as **"P7M Attachment
   Converter"** in the tool list.

## Usage

1. Connect to your Dynamics 365 / Dataverse environment in
   XrmToolBox.
2. Open **P7M Attachment Converter**.
3. Click **Load P7M Attachments** to scan for `.p7m` files.
4. Check the rows you want to convert.
5. Click **Convert Selected**. Watch the Status column for results.

## Building from source

- Visual Studio 2022+ with the **.NET desktop development** workload
- **.NET Framework 4.8** targeting pack
- NuGet packages: `XrmToolBoxPackage`, `Microsoft.CrmSdk.CoreAssemblies`,
  `Microsoft.CrmSdk.XrmTooling.CoreAssembly`

```
git clone https://github.com/praveenellappa/Praveen.XrmToolbox.P7mAttachmentConverter.git
```
Open the `.sln` in Visual Studio, restore NuGet packages, build.

## Tech stack

- C# / .NET Framework 4.8
- Windows Forms
- `System.Security.Cryptography.Pkcs` (PKCS#7/CMS decoding)
- Microsoft Dynamics 365 SDK (`Microsoft.Xrm.Sdk`)
- XrmToolBox Extensibility framework

## Author

Built by **Praveen Kumar Ellappa** — CRM Architect specializing in
Microsoft Dynamics 365 and the Power Platform.

## License

MIT — free to use, modify, and distribute.
