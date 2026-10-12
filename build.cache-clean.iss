// 升级安装时的缓存清理（被 build.iss 的 [Code] 段 #include；本文件不含任何节标题）。
// 抽成独立文件是为了能用沙箱安装包单独跑一遍验证删除/保留规则——这里删错东西的代价是用户的工程音频。

// 文件名是否以 P1 / P2 开头（空串＝不比较）
function KeepsName(const Name, P1, P2: String): Boolean;
begin
  Result := ((P1 <> '') and (CompareText(Copy(Name, 1, Length(P1)), P1) = 0))
         or ((P2 <> '') and (CompareText(Copy(Name, 1, Length(P2)), P2) = 0));
end;

// 删除目录下（不含子目录）除保留项以外的所有文件
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

// 删除目录下的所有子目录（缓存子目录整棵删：cloud\lyrics 等）
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

// 清掉数据目录下的缓存：旧版日志、云音乐下载（含歌词缓存与跟随匹配缓存）、工程包解包出来的音频副本。
// 这些目录程序启动后按需重建，升级从干净状态开始，不再命中旧版留下的试听片段 / 同名错配文件。
// 两份用户数据例外保留：
//   cloud\follow_state.json        跟随对齐偏移（用户手调的秒数）
//   cache\audio\autosave_song.*    自动恢复用的音频备份（原文件被移走/删掉时可能是唯一一份）
//   以及它的去重记录 cache\audio\.autosave.json（删了会白拷一遍，不影响正确性）
procedure CleanCachesUnder(const DataRoot: String);
begin
  DelTree(DataRoot + '\logs', True, True, True);
  DeleteSubDirs(DataRoot + '\cloud');
  DeleteFilesExcept(DataRoot + '\cloud', 'follow_state.json', '');
  DeleteSubDirs(DataRoot + '\cache\audio');
  DeleteFilesExcept(DataRoot + '\cache\audio', 'autosave_song.', '.autosave.json');
end;
