 ## Purpose
 To record errors made by Claude in this project.
 Claude should read this file before generating any code
 and avoid repeating patterns marked as incorrect.

 ## [001] — Archivos .md compilados como C#
**Fecha**: 2025-01-01
**Error**: Visual Studio compila archivos .md como C# 
           cuando se agregan al proyecto
**Solución**: Siempre establecer Build Action = None 
              en archivos .md
**Regla**: Todo archivo .md agregado al proyecto debe 
           tener Build Action = None