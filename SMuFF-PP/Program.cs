/**
 * SMuFF-PP - Post processor for GCode files.
 * Copyright (C) 2021-2023 Technik Gegg
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

using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;

namespace smuff_pp
{
    class Program
    {
        static bool verbose = true;
        static double threshold = 0;
        static double skipThreshold = 0;
        static String purgeCode;

        static String preToolChangeCode;
        static String postToolChangeCode;
        static Int32 firstToolChange = 0;
        static Int32 relocationsOk = 0;
        static Int32 relocationsFailed = 0;
        static Int32 toolChanges = 0;
        static Int32 toolChangesSkipped = 0;
        static String toolRegex = @"^[T]\d(.*)$";
        static String slicerRegex = @"^(.*?(.enerated\s(with|by))\s(\b.+\b)[^$]*)$";
        static String extrusionRegex = @"G[1|2|3|5].([X].*|[Y].*)([E][+-]?(\d+([.]\d*)?(e[+-]?\d+)?|[.]\d+(e[+-]?\d+)?))";
        static String featureRegex = @"^; (feature)\s(\b.*\b)$";
        
        /**
            Main program entry point.
            Will return 1 if an output file was sucessfully created, a 0 if issues have occured while processing. 
        */
        static int Main(string[] args)
        {
            var ci = new CultureInfo("en-US");
            CultureInfo.DefaultThreadCurrentCulture = ci;   // needed for floating point conversions of the extrusions

            var ver = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            Console.WriteLine("\n\n>>> SMuFF-PP - GCode post processor for purge-less multi material printing.\n>>> Version {0}\n", ver);
            
            if(args.Length < 1) {
                writeError("Missing input file!\n");
                Console.WriteLine("Usage:\n\tSMuFF-PP [-v] [-t=nnn] [-tskip=nnn] input-gcode-file");
                Console.WriteLine("Options:");
                Console.WriteLine("  -v\t\tPrint all processing information.");
                Console.WriteLine("  -t=nnn\tAmount in mm of filament needs to be purged (a.k.a. length of the hotend).");
                Console.WriteLine("  -skip=nnn\tAmount of filament in mm that will cause skipping a tool change if less.");
                return 0;
            }
            
            if(!ReadSettings(AppContext.BaseDirectory)) {
                writeWarning("No Settings found! Using default settings.");
            }

            var inputFile = args[args.Count()-1];
            // lookup command line parameters and override default settings
            if(args.Length >= 2) {
                foreach(var arg in args) {
                    if(arg.Equals("-v")) {
                        verbose = true;
                    }
                    if(arg.StartsWith("-t=")) {
                        Double.TryParse(arg.Substring(3), out threshold);
                    }
                    if(arg.StartsWith("-skip=")) {
                        Double.TryParse(arg.Substring(6), out skipThreshold);
                    }
                }
            }
            if(threshold == 0) {
                writeError("Threshold value is not defined! Aborting.");
                return 0;
            }
            if(!Path.Exists(inputFile)) {
                writeError(String.Format("Input file '{0}' not found. Please check and try again!", inputFile));
                return 0;
            }

            var outputFile = Path.GetFileNameWithoutExtension(inputFile);
            outputFile = Path.GetDirectoryName(inputFile) + Path.DirectorySeparatorChar + outputFile + "_(SMuFF)" + Path.GetExtension(inputFile);

            Console.WriteLine("Input file:\t\t\"{0}\"", inputFile);
            Console.WriteLine("Output file:\t\t\"{0}\"", outputFile);
            Console.WriteLine("Purge threshold:\t{0:N3}", threshold);
            Console.WriteLine("Skip threshold:\t\t{0:N3}", skipThreshold);

            if(ReadInput(inputFile, outputFile)) {
                if(verbose) {
                    Console.WriteLine("\nDone parsing input file, output file written.");
                }
                Console.WriteLine("----------------------------------------------------------------------------------------------");
                Console.WriteLine("Statistics: {0} tool change(s); {1} skipped; {2} relocation(s) accomplished; {3} purge(s) needed.", toolChanges, toolChangesSkipped, relocationsOk, relocationsFailed);
                Console.WriteLine("----------------------------------------------------------------------------------------------");
            }
            else {
                Console.WriteLine("\nErrors encountered while reading '{0}'.", inputFile);
                return 0;
            }
            return 1;
        }

        private static void writeError(String text) {
            Console.WriteLine("Error: " + text);
        }
        private static void writeWarning(String text) {
            Console.WriteLine("Warning: " + text);
        }

        private static void writeInfo(String text) {
            Console.WriteLine("Info: " + text);
        }

        private static void writeDefaultPattern(String pattern) {
            Console.WriteLine("Using default {0}-RegEx pattern!", pattern);
        }

        /**
            Checks the validity of an regex pattern
        */
        private static bool isRegexValid(string pattern)
        {
            if(string.IsNullOrWhiteSpace(pattern)) {
                writeError(String.Format("RegEx pattern '{0}' must not be empty.", pattern));
                return false;
            }
            try {
                Regex.Match("", pattern);
            }
            catch (ArgumentException e) {
                writeError(String.Format("RegEx pattern '{0}' is invalid. Reason: {1}", pattern, e.Message));
                return false;
            }
            return true;
        }

        /**
            Reads the settings from the XML file.
        */
        private static bool ReadSettings(String path)
        {
            var xdoc = new XmlDocument();
            var cfgFile = Path.Combine(path, "settings.xml");
            try {
                xdoc.Load(cfgFile);
                var node = xdoc.SelectSingleNode("/root/Verbose");
                if(node != null)
                    Boolean.TryParse(node.InnerText, out verbose);

                node = xdoc.SelectSingleNode("/root/Threshold");
                if(node != null && !String.IsNullOrEmpty(node.InnerText) && !Double.TryParse(node.InnerText, out threshold)) {
                    writeError("Max. threshold value must be a real number (i.e. 76.0)");
                    return false;
                }
                
                node = xdoc.SelectSingleNode("/root/SkipThreshold");
                if(node != null && !String.IsNullOrEmpty(node.InnerText) && !Double.TryParse(node.InnerText, out skipThreshold)) {
                    writeError("Skip threshold value must be a real number (i.e. 4.5)");
                    return false;
                }

                // get the GCode used for purging if relocation is no option
                node = xdoc.SelectSingleNode("/root/PurgeCode");
                if(node != null && !String.IsNullOrEmpty(node.InnerText)) {
                    purgeCode = String.Format(node.InnerText, threshold);
                }
                else {
                    writeWarning("No Purge GCode assigned!");
                }

                // get the GCode to be inserted before the (relocated) tool change happens
                node = xdoc.SelectSingleNode("/root/PreToolChangeCode");
                if(node != null && !String.IsNullOrEmpty(node.InnerText)) {
                    preToolChangeCode = node.InnerText;
                }

                // get the GCode to be inserted after the (relocated) tool change happens
                node = xdoc.SelectSingleNode("/root/PostToolChangeCode");
                if(node != null && !String.IsNullOrEmpty(node.InnerText)) {
                    postToolChangeCode = node.InnerText;
                }
                
                // get the regex patterns...
                // ...for finding tool change commands
                node = xdoc.SelectSingleNode("/root/RegexPatterns/Tool");
                if(node != null && !String.IsNullOrEmpty(node.InnerText)) {
                    if(isRegexValid(node.InnerText)) {
                        toolRegex = node.InnerText;
                    }
                }
                else {
                    writeDefaultPattern("Tool");
                }
                // ...for finding the Slicer that generated the GCode
                node = xdoc.SelectSingleNode("/root/RegexPatterns/Slicer");
                if(node != null && !String.IsNullOrEmpty(node.InnerText)) {
                    if(isRegexValid(node.InnerText)) {
                        slicerRegex = node.InnerText;
                    }
                }
                else {
                    writeDefaultPattern("Slicer");
                }
                // ...for finding extrusions GCode (i.e. G1 Exxx)
                node = xdoc.SelectSingleNode("/root/RegexPatterns/Extrusion");
                if(node != null && !String.IsNullOrEmpty(node.InnerText)) {
                    if(isRegexValid(node.InnerText)) {
                        extrusionRegex = node.InnerText;
                    }
                }
                else {
                    writeDefaultPattern("Extrusion");
                }
                // ...for finding features (i.e. Ooze-Shield, Brim, ...)
                node = xdoc.SelectSingleNode("/root/RegexPatterns/Feature");
                if(node != null && !String.IsNullOrEmpty(node.InnerText)) {
                    if(isRegexValid(node.InnerText)) {
                        featureRegex = node.InnerText;
                    }
                }
                else {
                    writeDefaultPattern("Feature");
                }
            }
            catch(Exception e) {
                writeError(String.Format("Can't open the settings file: {0}", e.Message));
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
            Regex toolRx = new Regex(toolRegex, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            Regex slicerRx = new Regex(slicerRegex, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var gcodeLines = new LinkedList<string>();
            bool skipNextTool = false;
            
            gcodeLines.AddLast(new LinkedListNode<string>(String.Format("; Post processed by SMuFF-PP {0}", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"))));
            gcodeLines.AddLast(new LinkedListNode<string>(String.Format("; SMuFF-PP params: Purge threshold: {0:N2}mm; Skip threshold:  {1:N2}mm", threshold, skipThreshold)));

            try {
                using(StreamWriter wr = new StreamWriter(outputFile)) {
                    try {
                        Match match;

                        using (StreamReader sr = new StreamReader(inputFile)) {
                            string line;
                            Int32 linesCounter = 0; 
                            do {
                                line = sr.ReadLine();
                                if(line == null)
                                    break;
                                linesCounter++;
                                // check for Slicer name (Simplify, Prusa or Cura)
                                match = slicerRx.Match(line);
                                if(match.Length > 0) {
                                    if(verbose)
                                        Console.WriteLine("Slicer:\t\t\t{0}", match.Groups[4].Value);
                                }
                                gcodeLines.AddLast(line);
                                // check for tool change
                                match = toolRx.Match(line);
                                if(match.Length > 0) {
                                    if(firstToolChange == 0) {
                                        firstToolChange = linesCounter;
                                        if(verbose)
                                            Console.WriteLine("\nPrinting starts at line {0} with {1}", firstToolChange, match.Value);
                                    }
                                    else {
                                        if(verbose)
                                            Console.Write("{1} in line {0}: ", linesCounter, match.Value);
                                        toolChanges++;
                                        skipNextTool = false;
                                        if(toolChanges > 1 && skipThreshold > 0) {
                                            // look ahead for next tool change
                                            double extrusion;
                                            if(ParseAhead(gcodeLines, linesCounter, out extrusion)){
                                                gcodeLines.Last.Value = "; " + gcodeLines.Last.Value + "\t; SMuFF-PP: Skipping tool change because of skip threshold";
                                                Console.WriteLine("\tSkip-threshold not reached (Amount: {0:N3}). Skipping this tool change.", extrusion);
                                                skipNextTool = true;
                                                toolChangesSkipped++;
                                            }
                                        }
                                        if(gcodeLines.Count > 0) {
                                            // when tool change is being skipped, no need for backward parsing extrusions
                                            if(!skipNextTool)
                                                ParseBackwards(gcodeLines, linesCounter);
                                            // write lines read into output file
                                            if(!WriteBufferdLines(wr, gcodeLines)) {
                                                // stop processing if writing fails
                                                return false;
                                            }
                                            // free memory of the lines buffered so far
                                            gcodeLines = new LinkedList<string>();
                                        }
                                    }
                                }
                            } while (line != null);
                                            
                            gcodeLines.AddLast(new LinkedListNode<string>(String.Format("\n; SMuFF-PP Statistics: {0} tool change(s); {1} skipped; {2} relocation(s) accomplished; {3} purge(s) needed.\n", toolChanges, toolChangesSkipped, relocationsOk, relocationsFailed)));
                            // write rest of buffer if needed
                            if(gcodeLines.Count > 0)
                                WriteBufferdLines(wr, gcodeLines);
                        }
                    }
                    catch (Exception e) {
                        writeError(String.Format("Can't read the input file: {0}", e.Message));
                        return false;
                    }
                }
            }
            catch(Exception e) {
                writeError(String.Format("Can't open the output file: {0}", e.Message));
                return false;
            }
            return true;
        }

        /**
            Write the (modified) buffered GCode lines to the output file
        */
        private static bool WriteBufferdLines(StreamWriter wr, LinkedList<string> gcodeLines)
        {
            var node = gcodeLines.First;
            Int32 linesWritten = 0;

            if(gcodeLines == null || node == null)
                return true;
            try {
                do {
                    wr.WriteLine(node.Value);
                    linesWritten++;
                    node = node.Next;
                } while(node != null);
                return true;
            }
            catch(Exception e) {
                writeError(String.Format("Can't write to output file. Reason: '{0}'", e.Message));
            }
            return false;
        }

        private static double GetExtrusionAmount(String line, Regex regex) {
            double extrusion = 0;
            // check line for a G1 with extrusion but ignore lines without X or Y coordinate, 
            // because these are supposedly retractions.
            Match match = regex.Match(line);
            if(match.Length > 0) {
                // it's a valid extrusion, get the amount
                double val = 0;
                if(Double.TryParse(match.Groups[3].Value, out val))
                    extrusion = val;
                else {
                    writeError(String.Format("TryParse mismatch in line '{0}'", line));
                }
            }
            return extrusion;
        }

        /**
            Parse the buffered GCode line by line backwards to calculate the extrusions
            and find the best place to relocate the tool change. 
        */
        private static bool ParseBackwards(LinkedList<string> gcodeLines, Int32 currLine)
        {
            double extrusion = 0;
            Int32 lineNr = currLine-1;
            Regex toolRx = new Regex(toolRegex, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            Regex extrusionRx = new Regex(extrusionRegex, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var status = false;
            LinkedListNode<string> node = gcodeLines.Last.Previous;
            LinkedListNode<string> markedNode = null;
            var tool = gcodeLines.Last.Value;

            do {
                var line = node.Value;
                // ignore comments completely
                if(line.StartsWith(";")) {
                    node = node.Previous;
                    lineNr--;
                    continue;
                }
                // get the extrusion
                extrusion += GetExtrusionAmount(line, extrusionRx);
                // have we reached the threshold value already?
                if(extrusion >= threshold) {
                    // yes, move the tool change command (Tx) into this position
                    if(verbose)
                        Console.WriteLine("\tRelocated by -{1:N3} mm (to line {2})", tool, extrusion, lineNr);
                    // mark the node for later on
                    markedNode = node;
                    status = true;
                    break;
                }

                // did we hit a previous tool change command while still parsing?
                Match match = toolRx.Match(line);
                if(match.Length > 0) {
                    // yes, stop parsing right here
                    if(verbose)
                        Console.WriteLine("\tPrevious tool change encountered ('{0}'). Stopping processing.", line);
                    break;
                }
                // go to the previous line and continue parsing
                node = node.Previous;
                lineNr--;
            } while(node.Previous != null);

            if(status && markedNode != null) {
                RelocateToolChange(markedNode, gcodeLines, tool, lineNr);
            }
            else {
                // relocation of the tool change couldn't be accomplished, we need to purge anyways!
                relocationsFailed++;
                if(!verbose)
                    Console.Write("\t{0} Relocation(s) ", relocationsFailed);
                else 
                    Console.Write("\tRelocation ");
                Console.Write("failed! Max. extrusion length is: {0:N3} mm. Purge code ", extrusion);
                // insert the "Purge Code", if one is given
                if(!String.IsNullOrEmpty(purgeCode)) {
                    gcodeLines.AddAfter(gcodeLines.Last, purgeCode);
                    Console.WriteLine("inserted.");
                }
                else {
                    Console.WriteLine("not inserted since no Purge-Code is available!");
                    gcodeLines.AddAfter(gcodeLines.Last, String.Format("; SMuFF-PP: add your PURGE code [ i.e. M83 E{0:N3} M82 ] here", threshold));
                }
            }
            return status;
        }

        /**
            Parses the buffered GCode line by line to determine whether the extrusions
            for the next tool are less than the skip threshold.
            This method is opnly called if the skip threshold is not 0.
            Returs true if they are less or false otherwise. 
        */
        private static bool ParseAhead(LinkedList<string> gcodeLines, Int32 lines, out double extrusion)
        {
            Regex extrusionRx = new Regex(extrusionRegex, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            LinkedListNode<string> node = gcodeLines.First;
            Int32 lineNr = lines;

            extrusion = 0;
            do {
                var line = node.Value;
                node = node.Next;
                // not interessted in comments, skip those
                if(line.StartsWith(";"))
                    continue;
                extrusion += GetExtrusionAmount(line, extrusionRx);
                // if the extrusion already exceeds the skip threshold, no need to parse any further
                if(extrusion > skipThreshold*1.2) {
                    extrusion = skipThreshold+1;
                    break;
                }
            } while(node.Next != null);
            return extrusion < skipThreshold;
        }

        /**
            Modify the output GCode with an relocated tool change
        */
        private static void RelocateToolChange(LinkedListNode<String> node, LinkedList<String> gcodeLines, String tool, Int32 lineNr) {
            String pre = String.IsNullOrEmpty(preToolChangeCode) ? "" : preToolChangeCode + "\n";
            String post = String.IsNullOrEmpty(postToolChangeCode) ? "" : "\n" + postToolChangeCode + "\n";
            gcodeLines.AddAfter(node, String.Format("{0}{1}\t; SMuFF-PP: relocated tool change{2}", pre, tool, post));
            gcodeLines.Last.Value = String.Format("; SMuFF-PP: Relocated {0} to line {1}", tool, lineNr);
            relocationsOk++;
        }
    }
}
