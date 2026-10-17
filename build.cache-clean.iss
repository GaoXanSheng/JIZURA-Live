
function KeepsName(const Name, P1, P2: String): Boolean;
begin
  Result := ((P1 <> '') and (CompareText(Copy(Name, 1, Length(P1)), P1) = 0))
         or ((P2 <> '') and (CompareText(Copy(Name, 1, Length(P2)), P2) = 0));
end;

procedure DeleteFilesExcept(const Dir, Keep1, Keep2: String);
var
  FindRec: TFindRec;
begin
  if not DirExists(Dir) then Exit;
  if FindFirst(Dir + '\*', FindRec) then
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
        if not KeepsName(FindRec.Name, Keep1, Keep2) then
          DeleteFile(Dir + '\' + FindRec.Name);
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

procedure DeleteSubDirs(const Dir: String);
var
  FindRec: TFindRec;
  Name: String;
begin
  if not DirExists(Dir) then Exit;
  if FindFirst(Dir + '\*', FindRec) then
  try
    repeat
      Name := FindRec.Name;
      if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0)
        and (Name <> '.') and (Name <> '..') then
        DelTree(Dir + '\' + Name, True, True, True);
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

procedure CleanCachesUnder(const DataRoot: String);
begin
  DelTree(DataRoot + '\logs', True, True, True);
  DeleteSubDirs(DataRoot + '\cloud');
  DeleteFilesExcept(DataRoot + '\cloud', 'follow_state.json', '');
  DeleteSubDirs(DataRoot + '\cache\audio');
  DeleteFilesExcept(DataRoot + '\cache\audio', 'autosave_song.', '.autosave.json');
end;
