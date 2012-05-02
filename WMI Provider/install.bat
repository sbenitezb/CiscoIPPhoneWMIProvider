net stop winmgmt /y
net start winmgmt
gacutil.exe -i CiscoPhoneWMIProvider.dll
installutil.exe CiscoPhoneWMIProvider.dll
