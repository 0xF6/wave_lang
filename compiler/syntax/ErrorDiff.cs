﻿namespace insomnia.syntax
{
    using System;
    using compilation;
    using Spectre.Console;

    public static class ErrorDiff
    {
        public static string DiffErrorFull(this Transform t, DocumentDeclaration doc)
        {
            try
            {
                var (diff, arrow_line) = DiffError(t, doc);
                return $"\n\t[grey] {diff.EscapeMarkup().EscapeArgumentSymbols()} [/]\n\t[red] {arrow_line.EscapeMarkup().EscapeArgumentSymbols()} [/]";
            }
            catch
            {
                return ""; // TODO analytic
            }
        }
        public static (string line, string arrow_line) DiffError(this Transform t, DocumentDeclaration doc)
        {
            var line = doc.SourceLines[t.pos.Line].Length < t.len ? 
                t.pos.Line - 1 : 
                /*t.pos.Line*/throw new Exception("cannot detect line");

            var original = doc.SourceLines[line];
            var err_line = original[(t.pos.Column - 1)..];
            var space1 = original[..(t.pos.Column - 1)];
            var space2 = (t.pos.Column - 1) + t.len > original.Length ? "" : original[((t.pos.Column - 1) + t.len)..];

            return (original,
                $"{new string(' ', space1.Length)}{new string('^', err_line.Length)}{new string(' ', space2.Length)}");
        }
    }
}