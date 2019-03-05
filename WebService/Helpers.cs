using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Text.RegularExpressions;

namespace WebService
{
    public class Helpers
    {
        public static string FrenchToAscii(string s)
        {
            var rExps = new[]
                            {
                                @"[\xC0-\xC2]",
                                @"[\xE0-\xE2]",
                                @"[\xC8-\xCA]",
                                @"[\xE8-\xEB]",
                                @"[\xCC-\xCE]",
                                @"[\xEC-\xEE]",
                                @"[\xD2-\xD4]",
                                @"[\xF2-\xF4]",
                                @"[\xD9-\xDB]",
                                @"[\xF9-\xFB]",
                                @"[\xC7]",
                                @"[\xE7]"
                            };

            var repChar = new[] { 'A', 'a', 'E', 'e', 'I', 'i', 'O', 'o', 'U', 'u', 'C', 'c' };

            for (var i = 0; i < rExps.Length; i++)
            {
                s = Regex.Replace(s, rExps[i], repChar[i].ToString());
            }
            return s.Replace('À', 'A')
                .Replace('à', 'a')
                .Replace('Â', 'A')
                .Replace('â', 'a')
                .Replace('Æ', 'A')
                .Replace('æ', 'a')
                .Replace('Ç', 'C')
                .Replace('ç', 'c')
                .Replace('È', 'E')
                .Replace('è', 'e')
                .Replace('É', 'E')
                .Replace('é', 'e')
                .Replace('Ê', 'E')
                .Replace('ê', 'e')
                .Replace('Ë', 'E')
                .Replace('ë', 'e')
                .Replace('Î', 'I')
                .Replace('î', 'i')
                .Replace('Ï', 'I')
                .Replace('ï', 'i')
                .Replace('Ô', 'O')
                .Replace('ô', 'o')
                .Replace('Œ', 'O')
                .Replace('œ', 'o')
                .Replace('Ù', 'U')
                .Replace('ù', 'u')
                .Replace('Û', 'U')
                .Replace('û', 'u')
                .Replace('Ü', 'U')
                .Replace('ü', 'u');
        }
    }
}