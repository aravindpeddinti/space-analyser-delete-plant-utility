SPACE ANALYZER
==============
A tool to find what is filling the disks on Windows machines and network VMs,
and clean it up safely.

This combined version also finds SEM smartplantv4.ini files on the machine being
scanned and lets you review plants by folder space and database space. To build
the combined app, install Python 3.10+ and requirements.txt, then run
BuildCombined.bat. Keep SpaceAnalyzer.exe and Delete Plant Utility CLI.exe
together. See the SEM sites and plants section in User Guide.html.

Double-click SpaceAnalyzer.exe and click "Yes" when Windows asks for permission.
The app opens full screen and scans the computer you are on.

See "User Guide.html" (open it in any web browser) for full instructions on:
- Cleanup advisor, Biggest folders, Biggest files, File types, By user
- Scanning network VMs and reviewing/deleting on them
- Scheduled maintenance that emails folder owners their old files
- The Recycle Bin browser, filtering, and safety features

Files the app keeps next to itself:
- SpaceAnalyzer_Log.csv      every deletion (time, user, machine, path, size)
- NetworkVMs.csv             your saved list of VMs
- TeamFolders.txt            parent folders for the By user analysis
- OwnerMap.csv               folder -> owner email (for scheduled emails)
- EmailSettings.ini          mail server settings (for scheduled emails)
- ScheduledVMs.txt           VMs included in the schedule
- SpaceAnalyzer_Schedule.log results of scheduled runs
- SpaceAnalyzer_Debug.log    technical log if something needs diagnosing
