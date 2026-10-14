# PrintToImage manual

PrintToImage is a network "virtual printer" for Windows. You install it once, on a server or any PC that is always on,
and it shares a printer called **\\\\SERVER\\PrintToImage**. The other PCs print to it like to any shared office
printer. Every print job is saved as a **PDF** (or **JPG** / **PNG**) named after the document, in one folder per PC.
From there, people or other programs (a document system, an EMR, an accounting package…) pick the files up.

```
 Any PC ──► \\SERVER\PrintToImage ──► PrintToImage service ──► D:\PrintToImage\Output\Reception\12345 Invoice.pdf
            (shared printer on the     (on the same server)     D:\PrintToImage\Output\Unassigned\...
             server, Windows PS driver)                          D:\PrintToImage\Output\Failed\...
```

Devices that cannot use a shared printer can instead print directly to the server's TCP port 9100 (section 6, method B).

**No Ghostscript and no other software is needed.** PrintToImage contains its own PostScript interpreter, so the
server only needs the files in this folder. (The .NET runtime is built in as well.)

**License:** free for personal and noncommercial use; businesses need a commercial license (section 10).

---

## 1. How a job is handled

1. The PC prints to the shared printer on the server. The server's Windows print queue passes the job to the
   PrintToImage service (over 127.0.0.1:9100). Devices may also send directly to TCP **9100** (or **515**, LPR, if enabled).
   If the service is stopped, jobs wait in the Windows print queue and are delivered when it runs again.
2. The **device** is identified by the PC's **computer name** (jobs through the shared printer; the service asks
   the Windows spooler who sent the job) or by its **IP address** (jobs sent directly). Both can be listed under
   `Devices`. Unknown PCs → `UnknownDevice`.
3. The **job name** is read from `@PJL JOB NAME="…"`, otherwise from the PostScript `%%Title:` comment
   (escaped and non-English names are decoded; for LPR jobs the control-file job name is also used).
4. The name is cleaned: application prefixes such as `Microsoft Word - ` and original extensions such as `.docx`
   are removed, and characters not allowed in file names are replaced by `_`.
5. The **file number** is taken from the name with a configurable regular expression (default: the first run of digits).
   Optionally (`PageNumberRegex`) also from the text **printed on the page**, for devices that name every print after
   the report rather than the patient (an IOLMaster prints `IOL-SRK-T` with the patient's ID on the page).
6. The job is converted to PDF (or JPG) and placed:

| Situation | Where it goes | File name |
|---|---|---|
| Name and file number found (or, with `PageNumberRegex`, a number on the page) | `Output\<Device>\` | the cleaned name, e.g. `12345 Invoice.pdf` |
| No name, or no file number in it (nor on the page) | `Output\Unassigned\` | `<Device>_<yyyyMMdd_HHmmss>.pdf` |
| The job could not be converted | `Output\Failed\` | the raw job (`.prn`) plus any partial output |
| Name already exists | same folder | ` (2)`, ` (3)` … added |
| JPG/PNG, several pages | same folder | `name_p1.jpg`, `name_p2.jpg` … (one image per page) |
| PDF with `PdfOneFilePerPage`, several pages | same folder | `name_p1.pdf`, `name_p2.pdf` … |

PrintToImage never guesses a file number: anything uncertain goes to **Unassigned** for a person to check.

Files are written under a hidden temporary name (`~PrintToImage_….tmp`) and renamed only when complete, so no program
ever sees a half-written file. Every received job is also kept as raw data in the archive for 30 days.

---

## 2. What is in the folder

```
PrintToImage\
  Install PrintToImage.bat          <- double-click to install or upgrade
  Open PrintToImage Settings.bat    <- double-click to change settings and see recent jobs
  Uninstall PrintToImage.bat        <- double-click to remove PrintToImage
  README.txt                        <- this guide
  files\                            <- used by the .bat files; no need to open
```

You only use the three `.bat` files. They ask for administrator rights themselves.

The `files` folder contains the programs and setup scripts (they are copied to `C:\Program Files\PrintToImage`):

| File | What it is |
|---|---|
| `PrintToImage.exe` | the PrintToImage service: receives print jobs and saves them (also has troubleshooting commands, section 7) |
| `libSkiaSharp.dll` | drawing library used by PrintToImage.exe (must stay next to it) |
| `PrintToImageSettings.exe` | the settings app |
| `SendTestPrint.exe` | test tool: sends a test print job, like a PC would (section 5) |
| `install.ps1` / `uninstall.ps1` | the installer and uninstaller (run by the `.bat` files) |
| `shared-printer.ps1` | creates / repairs / removes the shared printer `\\SERVER\PrintToImage` (run by the installer and the settings app) |
| `settings.sample.json` | default settings; copied to `C:\ProgramData\PrintToImage\settings.json` on the first installation |
| `LICENSE.txt` / `THIRD-PARTY-NOTICES.txt` | PrintToImage's license and the licenses of the open-source parts it contains (section 10) |
| `WHAT ARE THESE FILES.txt` | the same list, for anyone who opens the folder |

After installation, PrintToImage keeps its data in:

| Folder | Contents |
|---|---|
| `C:\Program Files\PrintToImage` | the installed programs |
| `C:\ProgramData\PrintToImage` | `settings.json`, `logs\`, `archive\` (received jobs, 30 days), `work\` (temporary) |
| `D:\PrintToImage\Output` (your choice) | the saved PDF/JPG/PNG files, one folder per device |

---

## 3. Install on the server

Requirements: Windows 10/11 or Windows Server 2016+ (64-bit). Nothing else to install.

1. Copy the whole `PrintToImage` folder to the server (for example to `C:\Install\PrintToImage`).
2. Double-click **`Install PrintToImage.bat`**, allow the administrator prompt and answer the questions
   (output folder, reader account — Enter to skip, paper size). At the end it offers to open the settings app.
   Running it again later upgrades PrintToImage and keeps your settings.

   Or, with all options, open **PowerShell as administrator** in that folder and run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\files\install.ps1 `
       -OutputRoot "D:\PrintToImage\Output" `
       -EmrAccount "OFFICE\svc-import" `
       -AllowedRemoteAddress "192.168.1.20,192.168.1.21,192.168.1.22"
   ```

   | Parameter | Meaning |
   |---|---|
   | `-OutputRoot` | Output folder (used when settings.json is created the first time). Default `D:\PrintToImage\Output`. |
   | `-EmrAccount` | The *reader account*: the Windows user or group of the program that reads the files (a document system, an EMR…). It gets read/modify rights on the output folder. **Without it, only administrators can open the output folder.** |
   | `-AllowedRemoteAddress` | Who may send print jobs (firewall). Default `LocalSubnet`. Listing the PCs' IP addresses is safer. |
   | `-PaperSize` | Paper size of the shared printer: `A4` (default) or `Letter`. |
   | `-NoSharedPrinter` | Do not create the shared printer (only if every device prints directly to port 9100). |
   | `-EnableLpd` | Also accept LPR jobs on TCP 515. |
   | `-ServiceAccount` | Default `NT SERVICE\PrintToImage` (a restricted account). Use `LocalSystem` only if the output folder is on another computer's share. |

   The script:
   - copies the program to `C:\Program Files\PrintToImage`
   - creates `C:\ProgramData\PrintToImage` (settings, logs, archive) and the output folders
   - **restricts both folders** (they may hold confidential documents): only SYSTEM, Administrators, the service and the reader account
   - installs the Windows service **PrintToImage** (starts automatically, restarts itself after a failure)
   - adds Windows Firewall rules for TCP 9100 (and 515 with `-EnableLpd`) for devices that print directly
   - starts the service and checks that it listens on 9100
   - **creates the shared printer `\\SERVER\PrintToImage`**: a print queue with the colour PostScript driver built into
     Windows ("MS Publisher Color Printer") whose port sends to the service on 127.0.0.1:9100. For this it enables the
     **Print Spooler** service and the **File and Printer Sharing** firewall rules (domain and private networks only).
     The script `shared-printer.ps1` (or the settings app) can repair or remove it later.
   - adds **PrintToImage Settings** to the Start menu and the desktop

3. Open **PrintToImage Settings** (Start menu or desktop):
   - **Shared printer** tab: check the status says *ready* and note the network path (`\\SERVER\PrintToImage`).
   - **Devices** tab: list the PCs by **computer name** (and/or IP address) with their device folder name.
   - Click **Save and restart service**.
4. Connect each PC to the shared printer (section 6).

(You can also edit `C:\ProgramData\PrintToImage\settings.json` by hand and run `Restart-Service PrintToImage`.)

To **upgrade**, double-click `Install PrintToImage.bat` in the new version's folder. Your settings are kept.

> If the output folder is on a network share (another server), run the service as an account that can write to the share:
> install with `-ServiceAccount LocalSystem` and give the server's computer account (`DOMAIN\SERVERNAME$`) write access to the share,
> or change the service's log-on account to a domain service account in *services.msc*.

---

## 4. Settings

### The settings app (recommended)

Open **PrintToImage Settings** from the Start menu or the desktop, or double-click **`Open PrintToImage Settings.bat`**
(it asks for administrator rights).

| Tab | What you can change |
|---|---|
| **Output** | output folder, format (PDF / JPG / PNG), one PDF per page or one per job, image resolution and JPG quality, file name pattern, the reader account that may open the output folder, names of the special folders |
| **Shared printer** | status and network path of `\\SERVER\PrintToImage`, its name and paper size, *Create / repair*, *Print a Windows test page*, *Remove*, and how to connect the PCs |
| **Devices** | PCs: computer name or IP address → device folder name, and per device its own format and PDF page setting. *Add unknown senders from the job log* adds PCs that already printed but are not listed yet |
| **Job names** | how the file number is found (ready-made choices or your own pattern), with a **live test**: type a document name and see the file number and the file name it will get; name cleaning rules; character set for non-English names |
| **Network** | printer port, LPR, who may print (only the listed PCs / local network / your own list) and limits |
| **Archive & logs** | archive and log folders and how long files are kept |
| **Service & jobs** | service status with start / stop / restart, **send a test print**, and the list of recent jobs (green = saved, orange = no file number, red = failed); double-click a job to open its file |

**Save** checks everything first and explains any problem. It then also creates the output folders with the right
permissions, updates the firewall rule and offers to restart the service (or use **Save and restart service**).
The previous settings are kept as `settings.json.bak`.

### The settings file

The app writes `C:\ProgramData\PrintToImage\settings.json`; you can also edit it by hand — restart the service after
changing it. If the file is invalid the service does not start and the reason is in the Windows *Application* event log.

| Setting | Default | Description |
|---|---|---|
| `RawPort` | `9100` | Raw / JetDirect port |
| `EnableLpd`, `LpdPort` | `false`, `515` | LPR/LPD listener (the Windows "LPD Print Service" must not be running) |
| `ListenAddress` | `0.0.0.0` | Network address to listen on (all) |
| `OutputRoot` | `D:\PrintToImage\Output` | Root output folder |
| `OutputFormat` | `pdf` | `pdf`, `jpg` or `png` (see "Choosing PDF, JPG or PNG" below) |
| `DeviceOutputFormat` | `{}` | Per-device format, e.g. `{ "Lab": "jpg", "Warehouse": "png" }` |
| `PdfOneFilePerPage` | `false` | PDF: `false` = all pages in one PDF, `true` = one PDF file per page |
| `DevicePdfOneFilePerPage` | `{}` | Per-device override, e.g. `{ "Reception": true }` |
| `JpgDpi`, `JpgQuality` | `300`, `90` | Resolution of JPG **and PNG** images; JPG quality 1–100 (PNG is always lossless) |
| `Devices` | see below | Sender IP → device folder name |
| `FileNumberRegex` | `\d+` | How to find the file number in the job name (see below) |
| `PageNumberRegex` | `""` (off) | How to find a file number **printed on the page** (see below). Same groups as `FileNumberRegex` |
| `FileNameTemplate` | `{name}` | Output file name: `{name}` cleaned name, `{number}` file number, `{page}` the number found on the page, `{device}`, `{date}` (yyyyMMdd), `{time}` (HHmmss). A part that is empty takes one separator with it (`{page}_{name}` without a number on the page → `IOL-SRK-T`) |
| `StripPatterns` | Word/Excel/PowerPoint/Acrobat… prefixes | Regular expressions removed from the job name |
| `StripExtensions` | `.pdf`, `.docx`, `.jpg`, … | Extensions removed from the end of the job name |
| `FallbackCodePage` | `0` | Code page for non-English job names that are not UTF-8. `0` = the server's Windows language. Examples: `1256` Arabic, `1252` Western, `1251` Cyrillic |
| `UnassignedFolderName`, `UnknownDeviceFolderName`, `FailedFolderName` | `Unassigned`, `UnknownDevice`, `Failed` | Special folder names |
| `ArchiveRetentionDays` | `30` | Days to keep raw jobs in the archive (`0` = no archive) |
| `ArchiveFolder`, `LogFolder`, `WorkFolder` | under `C:\ProgramData\PrintToImage` | |
| `LogRetentionDays` | `90` | Days to keep `service-*.log` (the `jobs-*.csv` job log is kept) |
| `RenderTimeoutSeconds` | `300` | A job that takes longer is moved to `Failed` |
| `MaxJobSizeMB`, `ReceiveIdleTimeoutSeconds`, `MaxConcurrentConversions` | `1024`, `300`, `2` | Limits |

**Choosing PDF, JPG or PNG**

| Format | Result | Good for |
|---|---|---|
| `pdf` (default) | one file per job with all pages; sharp text and lines at any zoom, searchable text, images lossless | documents and reports; most document systems and EMRs |
| `jpg` | one image per page; small files, slight compression loss | systems that only accept images |
| `png` | one image per page; lossless, larger files (often 3–10× JPG) | images where no compression loss is acceptable |

For every device, in `settings.json`:

```json
"OutputFormat": "png",
```

For some devices only (use the device folder names from `Devices`; all others use `OutputFormat`):

```json
"OutputFormat": "pdf",
"DeviceOutputFormat": { "Lab": "jpg", "Warehouse": "png" },
```

Image resolution for JPG/PNG: `"JpgDpi": 300` (150 = smaller files, 600 = very sharp but large).

**One PDF per job, or one PDF per page?** By default all pages of a print job go into one PDF (`12345 Invoice.pdf`).
To get a separate PDF for every page (`12345 Invoice_p1.pdf`, `12345 Invoice_p2.pdf`, …; a one-page job is still `12345 Invoice.pdf`):

```json
"PdfOneFilePerPage": true,
```

or only for some devices (the others keep one combined PDF):

```json
"PdfOneFilePerPage": false,
"DevicePdfOneFilePerPage": { "Reception": true },
```

(A job that a device sends as PDF instead of PostScript is always saved as received, not split.)

Save the file, then run `Restart-Service PrintToImage`.

**Devices** — each PC's computer name (shared printer) or IP address (direct printing) and its folder name:

```json
"Devices": {
  "RECEPTION-PC": "Reception",
  "LAB-PC": "Lab",
  "192.168.1.22": "Warehouse"
}
```

Computer names are matched without regard to case or domain suffix (`reception-pc.office.local` matches `RECEPTION-PC`). An IP
address also matches a shared-printer job if the computer name resolves to it. PCs that print directly need fixed IP
addresses (or DHCP reservations), otherwise their jobs land in `UnknownDevice`.

**FileNumberRegex** examples (in JSON every `\` is written `\\`):

| Job names look like | Regex | Notes |
|---|---|---|
| `Smith John 123456` | `\\d+` | default: first run of digits |
| `2024-03-01 Report 123456` (a date comes first) | `\\b\\d{6}\\b` | exactly 6 digits |
| `INV-004512 Acme` | `INV-(?<num>\\d+)` | the `num` group is used |
| `ID 98765` | `ID\\s*(\\d+)` | the first group is used |

**PageNumberRegex**: a number printed on the page. Some devices name every print after the report, never the
patient: an IOLMaster sends `IOL-SRK-T` or `MMT-Full`, with the patient's ID printed on the page. PrintToImage
reads the text it draws and uses the first match. Example for file numbers such as `D1935` or `J3107` (a letter or
two, then 3–7 digits; report codes such as `K1` are too short to match):

```json
"PageNumberRegex": "\\b[A-Za-z]{1,2}\\d{3,7}\\b",
"FileNameTemplate": "{page}_{name}_{date}_{time}",
```

→ `Output\IOLMaster\D1935_IOL-SRK-T_20261014_140229.pdf`. A job whose name has no file number is filed under its
device when the page has one; with neither it goes to **Unassigned** as before. Only PostScript jobs have page text
(a job received as PDF is saved as received). The job log has a `PageNumber` column. In the settings app: **Job
names → Number on the page**, with a box to try it on sample page text.

To see what name and file number a saved job would give, run:
`"C:\Program Files\PrintToImage\PrintToImage.exe" --check-name <job.prn>` (jobs are in the archive folder).

---

## 5. Test the service (no other PC needed)

On the server (or any PC that can reach it):

```powershell
cd "C:\Program Files\PrintToImage"
.\SendTestPrint.exe --title "Microsoft Word - 12345 Invoice.docx"        # -> Output\<device>\12345 Invoice.pdf
.\SendTestPrint.exe --title "Scan 778899" --pjl --pages 3                    # name in a PJL header, 3 pages
.\SendTestPrint.exe --title "No number here"                                 # -> Output\Unassigned\
.\SendTestPrint.exe --host 192.168.1.10 --title "Test 4455"                  # from another PC, to the server's IP
.\SendTestPrint.exe --title "Test 4455" --lpr                                # LPR (needs EnableLpd)
.\SendTestPrint.exe --file "C:\ProgramData\PrintToImage\archive\2024-05-01\job.prn"   # re-send a saved job
```

Jobs sent from the server itself come from `127.0.0.1`, so they go to `UnknownDevice` unless you add
`"127.0.0.1": "Test"` to `Devices`.

Then check:
- the output folder
- `C:\ProgramData\PrintToImage\logs\jobs-yyyy-MM.csv` — one line per job (time, sender IP, device, raw job name,
  file number, status, output file, error). Open it in Excel.
- `C:\ProgramData\PrintToImage\logs\service-yyyy-MM-dd.log` — detailed service log.

For testing without the service, `PrintToImage.exe --console` runs it in a console window
(stop the service first: both cannot use port 9100 at the same time).

---

## 6. Add the printer on each PC

The PCs need **no software**. In the examples the server is called **SERVER** (IP **192.168.1.10**) — use
your server's name and address. The settings app shows the exact network path on its *Shared printer* tab.

### Method A (recommended): connect to the shared printer `\\SERVER\PrintToImage`

The driver comes with the connection (it is the PostScript driver built into Windows), so there is nothing to choose.
The document name and the sending computer are taken from the server's Windows print queue (the installer gives the
PrintToImage service permission to read them), and a job is passed on only after it has been spooled completely.

**Windows 10 / 11**

1. **Settings → Bluetooth & devices → Printers & scanners → Add device**, wait a moment, then **Add manually**.
2. Choose **Select a shared printer by name**, type `\\SERVER\PrintToImage` → **Next**.
   (Or simply type `\\SERVER` in the File Explorer address bar and double-click **PrintToImage**.)
3. If Windows asks for a user name and password, use a Windows account of the server (see *Not in a domain* below).
4. **Finish**. Leave "Set as default printer" unticked unless wanted.

**Windows 7 / 8**

**Control Panel → Devices and Printers → Add a printer → Add a network printer → The printer that I want isn't listed →
Select a shared printer by name** → `\\SERVER\PrintToImage` → Next → Finish.

**Windows XP**

**Printers and Faxes → Add a printer → A network printer → Connect to this printer** → `\\SERVER\PrintToImage`.

**Not in a domain (workgroup / stand-alone PCs)**

Windows must be allowed to log on to the server:
1. On the server create a local user, e.g. `printuser`, with a password that does not expire
   (*Computer Management → Local Users and Groups*). It needs no special rights.
2. On each PC: **Control Panel → Credential Manager → Windows Credentials → Add a Windows credential**:
   Internet or network address `SERVER` (and also its IP address `192.168.1.10`), user `SERVER\printuser`, its password.
3. Connect the printer as above.

**If the connection fails**
- *"Windows cannot connect to the printer"* / *access denied*: check the credentials above and that the server's
  network is set to *Private* or *Domain* (File and Printer Sharing is only enabled for those).
- *Driver installation needs administrator rights*: connect the printer once while logged on as an administrator of
  the PC. (32-bit Windows 7/XP PCs may not get a driver from a 64-bit server — use method B for those.)
- If it still does not work, use method B.

Then list the PC on the **Devices** tab by its **computer name** (shown in *Settings → System → About* on the PC, or in
the *Sent from* column of the recent jobs once it has printed — *Add unknown senders from the job log* fills it in).

### Method B: print directly to TCP port 9100 (devices that cannot use a shared printer)

The PC gets its own printer with a **PostScript driver** built into Windows and a "Standard TCP/IP port" to
the server. List these PCs on the **Devices** tab by **IP address** (give them fixed addresses).

> **Important:** the driver must be a **PostScript (PS)** driver. PCL drivers ("HP Universal Printing PCL"),
> XPS drivers and "Microsoft Print to PDF" do not work; their jobs end up in `Output\Failed`.

#### Windows 10 / Windows 11

1. **Settings → Bluetooth & devices → Printers & scanners → Add device**. Wait a moment, then click
   **Add manually** ("The printer that I want isn't listed").
2. Choose **Add a printer using an IP address or hostname** → **Next**.
3. Device type: **TCP/IP Device**. Hostname or IP address: `192.168.1.10`.
   **Untick** "Query the printer and automatically select the driver to use" → **Next**.
4. Windows looks for the device and then asks for more information: choose **Custom** → **Settings…**
   - Protocol: **Raw**, Port Number: **9100**
   - **Untick "SNMP Status Enabled"** (otherwise Windows may show the printer as offline)
   - **OK** → **Next**.
5. Driver: Manufacturer **Generic** → **MS Publisher Color Printer** (a colour PostScript driver that is part of
   Windows), or **HP** → a colour model ending in **PS**. If the list is short, click **Windows Update** and wait.
   **Do not use "Microsoft PS Class Driver"**: it does not send the document name (every job would go to
   *Unassigned*) and it is black and white only.
6. Use the driver that is currently installed (if asked) → printer name e.g. **PrintToImage** → **Next**.
7. **Do not share** this printer → **Next**. Leave "Set as the default printer" unticked unless wanted → **Finish**.
8. Open the printer's **Printing preferences**: set the paper size you use (A4 or Letter) and colour.

#### Windows 7 / Windows 8

1. **Control Panel → Devices and Printers → Add a printer → Add a local printer**.
2. **Create a new port**, type **Standard TCP/IP Port** → **Next**.
3. Hostname or IP address `192.168.1.10`; **untick** "Query the printer…" → **Next**.
4. Device Type: **Custom** → **Settings…**: **Raw**, port **9100**, **untick SNMP Status Enabled** → **OK** → **Next**.
5. Driver: Manufacturer **Generic** → **MS Publisher Color Printer** (a PostScript driver).
   Alternatively choose **HP** and any model whose name ends in **PS**, for example
   *HP Color LaserJet 2800 Series PS* or *HP LaserJet 4250 PS*.
6. Name the printer **PrintToImage**, do not share it, finish.

#### Windows XP / older embedded systems

1. **Start → Printers and Faxes → Add a printer → Local printer attached to this computer**
   (untick "Automatically detect…").
2. **Create a new port: Standard TCP/IP Port** → Printer name or IP `192.168.1.10` → Device type **Custom → Settings**:
   **Raw**, port **9100**, SNMP off.
3. Driver: **HP** → a model ending in **PS**, e.g. **HP LaserJet 4000 Series PS**, or **Generic → MS Publisher Color Printer**.

### After adding the printer (both methods)

1. In any program, print a **test document** to **PrintToImage**.
2. On the server, open **PrintToImage Settings → Service & jobs** (or the last line of `logs\jobs-yyyy-MM.csv`):
   - **RawJobName** shows what document name the device sends.
   - If the file number is in it, the PDF is in `Output\<Device>\`.
   - If the device names its jobs without the file number (some programs use a fixed name such as "Report"), the jobs go
     to `Unassigned`. Check whether the program has a setting for the printed document name / title that can include
     the file number, or adjust `FileNumberRegex` if the number is there in a different form.
3. Windows' own "Print test page" is named "Test Page" (no number) and therefore goes to `Unassigned` — that is expected.

---

## 7. Troubleshooting

| Problem | What to check |
|---|---|
| Shared printer: nothing arrives | On the server open *Settings → Printers → PrintToImage → Open print queue*: jobs waiting there mean the service is stopped or its port is wrong — click *Create / repair* on the settings app's *Shared printer* tab and check that the service is running. |
| Shared printer: cannot connect | See *If the connection fails* in section 6 (credentials, network type *Private/Domain*, driver rights). |
| Shared printer: jobs from *UnknownDevice* / no document name | Run `"C:\Program Files\PrintToImage\PrintToImage.exe" --spooler` while a job waits in the queue (stop the service first): it shows what the queue reports. Re-run *Create / repair* (it sets the permission the service needs to read document names). |
| Direct printing: nothing arrives | `Test-NetConnection 192.168.1.10 -Port 9100` from the PC. The firewall setting (*Network* tab) must allow the PC. Is the service running? |
| Printer "offline" on the PC (method B) | Untick **SNMP Status Enabled** in the port settings. |
| Job in `UnknownDevice` | The PC is not on the *Devices* tab — compare with *Sent from* in the recent jobs (computer name for the shared printer, IP for direct printing); *Add unknown senders from the job log* adds it. |
| Job in `Unassigned` | No file number in the document name — see `RawJobName` in the job log. |
| Job in `Failed` | See the `Error` column. "not PostScript" = the PC uses a PCL/XPS driver: switch to a PS driver. The raw job is next to the error; send it to the developer. |
| Wrong/odd characters in non-English names | Set `FallbackCodePage` (e.g. `1256` for Arabic) and restart. |
| Text looks slightly different | Fonts that the job does not contain are replaced by the server's fonts (e.g. Helvetica → Arial). |
| Prints come out black and white | The printer's driver must be a colour driver: on the server the shared printer should use "MS Publisher Color Printer" (click *Create / repair* on the *Shared printer* tab to switch to it); in the print dialog choose *Color*. |
| Service does not start | Windows *Event Viewer → Application*, source *PrintToImage*; usually an error in settings.json. |

Re-convert a saved job by hand (also useful to reproduce a problem):

```powershell
& "C:\Program Files\PrintToImage\PrintToImage.exe" --render "C:\ProgramData\PrintToImage\archive\2024-05-01\job.prn" C:\Temp\out.pdf
& "C:\Program Files\PrintToImage\PrintToImage.exe" --render job.prn C:\Temp\pages --format png --dpi 300   # or --format jpg
& "C:\Program Files\PrintToImage\PrintToImage.exe" --render job.prn C:\Temp\pages --split   # one PDF per page
```

---

## 8. Uninstall

Double-click **`Uninstall PrintToImage.bat`**. It asks for confirmation, and whether to also delete the settings, logs
and job archive in `C:\ProgramData\PrintToImage`. It removes the service, the shared printer, the firewall rules,
the shortcuts and the program. The output folder with the saved documents is never deleted.

(Or in an elevated PowerShell: `powershell -ExecutionPolicy Bypass -File .\files\uninstall.ps1 [-RemoveData]`.)

---

## 9. Notes and limits

- **Supported input:** PostScript Level 1–3 as produced by Windows PostScript drivers (MS Publisher Color Printer,
  HP/Generic PS drivers, Microsoft PS Class Driver), with or without a PJL header. PDF jobs are saved as received (PDF output only).
  PCL, XPS and other printer languages are not supported.
- **Fonts:** fonts sent with the job (TrueType/Type 42, CID, Type 1, Type 3) are used. Printer-resident fonts are replaced by
  the server's fonts: Helvetica → Arial, Times → Times New Roman, Courier → Courier New, Symbol → Symbol.
- **PDF output** keeps text and lines as vectors with embedded font subsets (text can be searched and copied).
  Images are stored losslessly; JPEG photos in the job are kept as JPEG.
- **Copies:** a job asking for several copies is saved once.
- **Security:** jobs are only accepted from the addresses in the firewall rule. The output folder, logs and archive
  may contain confidential documents and are readable only by administrators, the service and the reader account.
  Job names (which may contain personal names) are written to the logs.

## 10. License

**Free for personal and noncommercial use** under the
[PolyForm Noncommercial License 1.0.0](https://github.com/baheras1993/PrintToImage/blob/main/LICENSE). This includes
charities, schools and universities, public hospitals and public health organisations, and government offices.

**Businesses need a commercial license.** It is a low, one-time price per server, with no per-user or per-PC fees.
A business may try PrintToImage for 30 days first. Details and how to get one:
<https://github.com/baheras1993/PrintToImage/blob/main/COMMERCIAL.md>

PrintToImage contains these open-source components, all of them free (also for commercial use):

| Component | License | Notes |
|---|---|---|
| .NET 8 runtime and libraries (built into the programs) | MIT | .NET Foundation |
| SkiaSharp / Skia (drawing and PDF writing, `libSkiaSharp.dll`) | MIT and BSD-3-Clause | includes FreeType, libpng, zlib, libjpeg-turbo, HarfBuzz and others, all permissive |

Their full license texts are in `THIRD-PARTY-NOTICES.txt` (in the package's `files` folder). **Ghostscript is not
used.** PrintToImage has its own PostScript renderer, so there are no AGPL obligations and no Ghostscript license to buy.

The Windows components it uses (Print Spooler, the "MS Publisher Color Printer" driver, the installed fonts) are part of
Windows and covered by the server's Windows license.

**No warranty.** As the license says, the software is provided "as is". Before relying on the saved documents, check
that they are complete and correct for each program and device. For medical use: PrintToImage is a filing tool, not a
medical device, so keep your EMR's own checks in place.

Source code, updates and building from source: <https://github.com/baheras1993/PrintToImage>
