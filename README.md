## SMuFF-PP - a Post Processor for GCode files used for multi material printing

This relatively simple app is a post processor which is supposed to be used in conjunction to your favorite slicer for 3D models.

This app is meant to reduce the amount of wasted material while 3D printing multi material/multi color models, a.k.a purged material. Here's a short video of the output generated by this app in action:

[![Video](http://img.youtube.com/vi/SHiTa84pGX0/0.jpg)](https://youtu.be/SHiTa84pGX0)

The GCode (original and generated) are contained in this repository for testing purposes.

Purging usually takes place after a tool change has been processed in order to get rid of the remains of the previous filament still sitting in the nozzle.
There are several different methods to achieve this. The most common method is to use a purge or prime tower along with your print.
Usually this is the most convenient method to set up but also the most wastefull. The purge or prime tower usually contains more material than the model itself.

Another method is just to purge out a certain amount of filament somewhere (i.e. a purge bin). This method requires some fine tuning in order to define the exact amount needed to avoid so called "color bleeding". Here's some useful information [on that topic](https://www.sublimelayers.com/2019/01/mosaic-palette-2-understand-and-control.html).

This app focuses on a 3rd method: It parses the produced GCode for tool changes (T1, T2, T3...) and tries to move them ahead of the original position. The distance of the movement can be defined in the **settings.xml** file with the **Treshold** parameters.
By moving the tool change a given extrusion amount before the original tool change takes place, the printer doesn't need to purge out the old filament but rather just continue printing.
In case it can't determine a decent relocation position, it's insert a code for purging out the old filament right after the tool change. This code tells the printer what to do (i.e. where to move for purging, or how many filament needs to be purged out) and has to be defined by you according to your printer and printing environment and put into the **settings.xml** file.

Here's an example for such a code:

```gcode
M83             ; set extruder to relative mode (important!)
G1 E{0} F340    ; Purge out old filament
M400            ; wait for move to finish
G4 P2500        ; wait for nozzle oozing out a bit
M82             ; set extruder back to absolute mode
```

The **G1 E** GCode in the example above is used to purge the filament out. The *{0}* parameter placeholder after **E** is meant to be the amount of extrusion in mm. You can just leave the paramater placeholder *{0}* there and the app will replace it by the **TresholdMax** value for you, or you may apply some absolute value to it. Letting the app replacing the value is the safer choice, since you have to change the value only once (in TresholdMax).

Be aware that you have to figure the correct treshold value yourself. It depends on a couple of different parameters, such as hotend and material used.
The value set in this configuration reflects the setup of a SMuFF using the [Filament-Cutter (V1)](https://www.thingiverse.com/thing:4650129) on a V6 hotend with an all-metal heatbreak.
Thus, the bowden tube in between the Filament-Cutter and hotend is 70 mm long. Add 15 mm for the cutting position and another 15 mm for the heatblock. That's round about 100 mm. The remaining 40-50 mm reflect the amount needed to avoid [color bleeding](https://www.sublimelayers.com/2019/01/mosaic-palette-2-understand-and-control.html).

For models that were sliced with the Simplify3D slicer, this app will try to relocate tool changes to where the "ooze shield" starts printing. For other slicers this method does not apply, since other slicers such as PrusaSlicer or Cura unfortunatelly do not explicit name the features (such as ooze shield).

For the **Duet3D/RRF** controller boards it's better to place the GCode shown above into a macro file (i.e. *smuff_pp_purge.g*) and call that macro by using:
```gcode
M98 P"smuff_pp_purge.g"             ; call purge macro
```
This enables you to change the amount of purged filament "on-the-fly" from within the DWC without the need of post processing the GCode once again.

***

## How to run this app

This program is a console app written in C# which utlizes the **.NET Core framework**. In order to run this app (as a DLL) open a command line window and type:

>dotnet run smuff-pp.dll *input_file*

Before you run it the first time, make sure you have set the parameters in your **settings.xml** file according to your 3D printers environment.

When you run it, the output will be something like this (if **Verbose** is set to **true**):

```text

==================================================================================================
SMuFF-PP - GCode post processor for faster multi material printing. Version Version 1.0 Beta
==================================================================================================
Using configuration from '...\bin\release\netcoreapp2.2\win-x64\settings.xml'.
Input file:     Columns.gcode
Purge treshold: 140.00 - 180.00
Generator:      Simplify3D(R) Version 4.1.2
Printing starts at line 212 with T0
Tool change at line 13650: T2
Found feature 'ooze shield' in line 10330. Extrusion now: 159.0701
Tool change at line 26477: T0
Found feature 'ooze shield' in line 23149. Extrusion now: 143.6495
Tool change at line 39278: T2
Found feature 'ooze shield' in line 35959. Extrusion now: 159.0703
Tool change at line 52118: T0
Found feature 'ooze shield' in line 48781. Extrusion now: 143.650
Tool change at line 64920: T2
Found feature 'ooze shield' in line 61591. Extrusion now: 159.0713
Tool change at line 77712: T0
Found feature 'ooze shield' in line 74415. Extrusion now: 143.6507

Done parsing input file, output written to 'Columns_(SMuFF).gcode'.
--------------------------------------------------------------
Statistics: 6 tool change(s), 6 relocation(s) done, 0 have failed.
--------------------------------------------------------------
```

It may differ based on the input file and your applied settings.

If you'd like an executable rather than a DLL, open a *Terminal* window in VSCode and key in:

>dotnet publish --configuration Release -r win-x64

This will produce an Windows 64-Bit executable file (i.e. smuff-pp.exe).

If you're on **MacOS** try using:

>dotnet publish --configuration Release **-r osx-x64**

On **Linux** you may use:

>dotnet publish --configuration Release **-r Linux-x64**

**Please notice**: *I haven't tested the MacOS/Linux options. I take Microsofts word for it. If you do, please let me know the outcome.*

After the app has been published, copy all the files from the *.\bin\release\netcoreapp2.2\win-x64* into your favorite folder and also copy the **settings.xml** file into that folder.

***

## Integration into your slicer

The most convenient way to use this app is to integrate it into your slicer. Most slicers nowadays do allow you running some post processing on the generated GCode file by defining an shell command, which gets executed after the GCode has been generated/saved.
Please refer to your favorite slicers documentation on how to achieve this.
