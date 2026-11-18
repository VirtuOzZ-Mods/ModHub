The Languages folder holds your own Mod Hub interface translations.

Russian, English and Ukrainian are built into the program; there is no need to put them here.
Extra translations go here as .json files.

How to make your own translation:
1. Settings → Interface language → "Create template". Save the JSON file:
   it contains every string of the program.
2. In the file change id (your own, without the official. prefix), culture (language code),
   displayName (the name shown in the list), author and version.
3. Translate the values in strings. Do not change the keys or the {0}, {1} parameters.
   Save the file as UTF-8.
4. Put the file into this folder, press "Refresh list" and choose your translation.

Untranslated strings come from the built-in translation of the same language, then from Russian.
