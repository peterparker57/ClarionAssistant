using System;
using System.Collections.Generic;
using System.IO;

namespace ClarionCodeGraph.Parsing
{
    /// <summary>
    /// Locates actual .clw/.inc source files on disk.
    /// For v1, uses the .\source\ subfolder convention.
    /// </summary>
    public class SourceResolver
    {
        /// <summary>
        /// Given a project directory and a list of source file names from the .cwproj,
        /// resolves each to its full path by searching known locations.
        /// </summary>
        public List<ResolvedFile> Resolve(string projectDir, List<string> fileNames)
        {
            var results = new List<ResolvedFile>();

            foreach (string fileName in fileNames)
            {
                string resolved = FindFile(projectDir, fileName);
                results.Add(new ResolvedFile
                {
                    FileName = fileName,
                    FullPath = resolved,
                    Found = resolved != null
                });
            }

            return results;
        }

        private string FindFile(string projectDir, string fileName)
        {
            // Search order:
            // 1. .\source\ subfolder (primary convention)
            // 2. Project root directory (fallback)

            string sourcePath = Path.Combine(projectDir, "source", fileName);
            if (File.Exists(sourcePath))
                return sourcePath;

            // Also try .\Source\ (case variation on case-insensitive Windows)
            string sourcePathAlt = Path.Combine(projectDir, "Source", fileName);
            if (File.Exists(sourcePathAlt))
                return sourcePathAlt;

            string rootPath = Path.Combine(projectDir, fileName);
            if (File.Exists(rootPath))
                return rootPath;

            return null;
        }
    }

    public class ResolvedFile
    {
        public string FileName { get; set; }
        public string FullPath { get; set; }
        public bool Found { get; set; }
    }
}
