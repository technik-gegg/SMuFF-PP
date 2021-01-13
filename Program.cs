/**
 * SMuFF-PP - Post processor for GCode files.
 * Copyright (C) 2021 Technik Gegg
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 *
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;


namespace smuff_pp
{
    class Program
    {
        const string VersionString = "Version 1.0 Beta";

        static bool verbose = true;
        static double tresholdMin = 0;
        static double tresholdMax = 0;
        static String purgeCode = null;
        static Int32 firstToolChange = 0;
        static Int32 relocationsOk = 0;
        static Int32 relocationsFailed = 0;
        static Int32 toolChanges = 0;

        /**
            Main program entry point.
            Will return 1 if an output file was sucessfully created, a 0 if issues have occured while processing. 
        */
        static int Main(string[] args)
        {
            var ci = new CultureInfo("en-US");
            CultureInfo.DefaultThreadCurrentCulture = ci;   // needed for floating point conversions of the extrusions

            Console.WriteLine("==================================================================================================");
            Console.WriteLine("SMuFF-PP - GCode post processor for faster multi material printing. Version {0}", VersionString);
            Console.WriteLine("==================================================================================================");

            if(!ReadSettings(AppContext.BaseDirectory)) {
                Console.WriteLine("\nSettings need to be checked! Aborting execution.");
                return 0;
            }

            if(args.Length < 1) {
                Console.WriteLine("Missing input file!");
                Console.WriteLine("Usage: smuff-pp {input-gcode-file}");
                return 0;
            }
            var inputFile = args[0];

            if(verbose) {
                Console.WriteLine("Input file:     {0}", inputFile);
                Console.WriteLine("Purge treshold: {0:N} - {1:N}", tresholdMin, tresholdMax);
            }
            var outputFile = Path.GetFileNameWithoutExtension(inputFile);
            outputFile = outputFile + "_(SMuFF)" + Path.GetExtension(inputFile);

            if(ReadInput(inputFile, outputFile)) {
                if(verbose) {
                    Console.WriteLine("\nDone parsing input file, output written to '{0}'.", outputFile);
                    Console.WriteLine("--------------------------------------------------------------");
                    Console.WriteLine("Statistics: {0} tool change(s), {1} relocation(s) done, {2} have failed.", toolChanges, relocationsOk, relocationsFailed);
                    Console.WriteLine("--------------------------------------------------------------");
                }
                else {
                    Console.WriteLine("Done.");
                }
            }
            else {
                Console.WriteLine("\nErrors encountered while reading '{0}'.", inputFile);
                return 0;
            }
            return 1;
        }

        /**
            Reads the settings from the XML file.
        */
        private static bool ReadSettings(String path)
        {
            var xdoc = new XmlDocument();
            var cfgFile = Path.Combine(path, "settings.xml");
            if(verbose)
                Console.WriteLine("Using configuration from '{0}'.", cfgFile);
            try {
                xdoc.Load(cfgFile);
                var node = xdoc.SelectSingleNode("/root/Verbose");
                if(node != null)
                    Boolean.TryParse(node.InnerText, out verbose);

                node = xdoc.SelectSingleNode("/root/TresholdMin");
                if(node != null && !String.IsNullOrEmpty(node.InnerText) && !Double.TryParse(node.InnerText, out tresholdMin)) {
                    Console.WriteLine("Error: Min. treshold value must be a real number (i.e. 202.5)");
                    return false;
                }

                node = xdoc.SelectSingleNode("/root/TresholdMax");
                if(node != null && !String.IsNullOrEmpty(node.InnerText) && !Double.TryParse(node.InnerText, out tresholdMax)) {
                    Console.WriteLine("Error: Max. treshold value must be a real number (i.e. 202.5)");
                    return false;
                }
                // swap min/max if they seem to be exchanged
                if(tresholdMax < tresholdMin) {
                    var tmp = tresholdMin;
                    tresholdMin = tresholdMax;
                    tresholdMax = tmp;
                }
                // get the GCode used for purging if relocation is no option
                node = xdoc.SelectSingleNode("/root/PurgeCode");
                if(node != null && !String.IsNullOrEmpty(node.InnerText)) {
                    purgeCode = String.Format(node.InnerText, tresholdMax);
                }
                else {
                    Console.WriteLine("Warning: No Purge GCode assigned!");
                }
            }
            catch(Exception e) {
                Console.WriteLine("Error: Can't open the settings file: {0}", e.Message);
                return false;
            }
            return true;
        }

        /** 
            Main process.
            Parses the input file for tool changes, buffers the GCode and generates the output file. 
        */
        private static bool ReadInput(String inputFile, String outputFile) 
        {
            Regex toolRx = new Regex(@"^[T]\d(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            Regex slicerRx = new Regex(@"^(.*?(.enerated\s(with|by))\s(\b.+\b)[^$]*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var gcodeLines = new LinkedList<string>();
            
            try {
                using(StreamWriter wr = new StreamWriter(outputFile)) {
                    try {
                        Match match;

                        using (StreamReader sr = new StreamReader(inputFile)) {
                            string line;
                            Int32 lineCounter = 0; 
                            do {
                                line = sr.ReadLine();
                                if(line == null)
                                    break;
                                lineCounter++;
                                // check for Slicer name (Simplify, Prusa or Cura)
                                match = slicerRx.Match(line);
                                if(match.Length > 0) {
                                    if(verbose)
                                        Console.WriteLine("Generator:      {0}", match.Groups[4].Value);
                                }
                                gcodeLines.AddLast(line);
                                // check for tool change
                                match = toolRx.Match(line);
                                if(match.Length > 0) {
                                    if(firstToolChange == 0) {
                                        firstToolChange = lineCounter;
                                        if(verbose)
                                            Console.WriteLine("Printing starts at line {0} with {1}", firstToolChange, match.Value);
                                    }
                                    else {
                                        if(verbose)
                                            Console.WriteLine("Tool change at line {0}: {1}", lineCounter, match.Value);
                                        toolChanges++;
                                        if(gcodeLines.Count > 0) {
                                            ParseBackwards(gcodeLines, lineCounter);
                                            // write lines read into output file
                                            WriteBufferdLines(wr, gcodeLines);
                                            // free memory of the lines buffered so far
                                            gcodeLines = new LinkedList<string>();
                                        }
                                    }
                                }
                            } while (line != null);
                            if(gcodeLines.Count > 0)
                                WriteBufferdLines(wr, gcodeLines);
                        }
                    }
                    catch (Exception e) {
                        Console.WriteLine("Error: Can't read the input file: {0}", e.Message);
                        return false;
                    }
                }
            }
            catch(Exception e) {
                Console.WriteLine("Error: Can't open the output file: {0}", e.Message);
                return false;
            }
            return true;
        }

        /**
            Write the (modified) buffered GCode lines to the output file
        */
        private static void WriteBufferdLines(StreamWriter wr, LinkedList<string> gcodeLines)
        {
            var node = gcodeLines.First;
            Int32 linesWritten = 0;

            if(gcodeLines == null || node == null)
                return;
            do {
                wr.WriteLine(node.Value);
                linesWritten++;
                node = node.Next;
            } while(node != null);
        }

        /**
            Parse the buffered GCode line by line backwards to calculate the extrusions
            and find the best place to relocate the tool change. 
        */
        private static bool ParseBackwards(LinkedList<string> gcodeLines, Int32 currLine)
        {
            double extrusion = 0;
            Int32 lineNr = currLine-1;
            Regex extrusionRx = new Regex(@"[E]([-]\d+.\d+|\d+.\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            Regex featureRx = new Regex(@"^; (feature)\s(\b.*\b)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            Match match;
            var status = false;
            LinkedListNode<string> node = gcodeLines.Last.Previous;
            LinkedListNode<string> markedNode = null;
            var tool = gcodeLines.Last.Value;


            do {
                var line = node.Value;
                // check line for a G1 (either a move or an extrusion)
                if(line.StartsWith("G1", StringComparison.InvariantCultureIgnoreCase)) {
                    // does this G1 have an E parameter?
                    match = extrusionRx.Match(line);
                    if(match.Length > 0) {
                        // yes, it's an extrusion. Add the extrusion amount, then move on parsing
                        // until a "move only" is found
                        double val;
                        if(Double.TryParse(match.Groups[1].Value, out val))
                            extrusion += val;
                    }
                    else {
                        // no, move only detected (i.e. without E parameter)
                        // have we reached the treshold value already?
                        if(extrusion > tresholdMax && markedNode == null) {
                            // yes, move the tool change command (Tx) into this position
                            if(verbose)
                                Console.WriteLine("Relocated {0} to -{1:N4} (Line {2})", tool, extrusion, lineNr);
                            // marke the node for later on
                            markedNode = node;
                            status = true;
                        }
                    }
                }
                // ------------------------------------------------------------------
                // look out for a feature "ooze shield" (applies only to Simplify3D sliced files)
                // since this is the optimal position for a tool change relocation.
                // Won't work for other slicers GCode, since the "ooze shield" feature isn't decalred explicitly.
                // ------------------------------------------------------------------
                match = featureRx.Match(line);
                if(match.Length > 0) {
                    // found a feature, Group[2] tells of what type the feature is
                    var type = match.Groups[2].Value;
                    // only if it's a "ooze shield" and we've reached the minimum treshold...
                    if(type.Equals("ooze shield") && extrusion >= tresholdMin) {
                        // relocate the tool change here
                        if(verbose)
                            Console.WriteLine("Found feature 'ooze shield' in line {0}. Extrusion now: {1:N4}", lineNr, extrusion);
                        status = true;
                        markedNode = node;
                        break;
                    }
                }
                // did we hit a tool change command?
                if(line.StartsWith("T", StringComparison.InvariantCultureIgnoreCase)) {
                    // yes, that's the previous tool change, stop parsing right here
                    if(verbose)
                        Console.WriteLine("Running into previous tool change '{0}'.", line);
                    break;
                }
                // go to the previous line and continue parsing
                node = node.Previous;
                lineNr -= 1;
            } while(node.Previous != null);

            if(status && markedNode != null) {
                RelocateToolChange(markedNode, gcodeLines, tool, lineNr);
            }
            // could the relocation of the tool change be accomplished?
            if(!status) {
                // nope, we need to purge anyways!
                // show a message
                if(verbose)
                    Console.WriteLine("Relocation not possible! Max. extrusion length is: {0:N4}", extrusion);
                relocationsFailed++;
                // and insert the "Purge Code" is one was given
                if(!String.IsNullOrEmpty(purgeCode))
                    gcodeLines.AddAfter(gcodeLines.Last, purgeCode);
                else
                    gcodeLines.AddAfter(gcodeLines.Last, String.Format("; SMuFF_PP: add your PURGE code [ i.e. M83 E{0:N4} M82 ] here", tresholdMax));
            }
            return status;
        }

        /**
            Modify the output GCode with an relocated tool change
        */
        private static void RelocateToolChange(LinkedListNode<String> node, LinkedList<String> gcodeLines, String tool, Int32 lineNr) {
            gcodeLines.AddAfter(node, String.Format("{0}\t; SMuFF_PP: relocated tool change", tool));
            gcodeLines.Last.Value = String.Format("; SMuFF_PP: Relocated {0} to line {1}", tool, lineNr);
            relocationsOk++;
        }
    }
}
