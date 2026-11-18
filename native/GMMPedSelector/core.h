#pragma once
#include <array>
#include <string>
#include <vector>
#include <sstream>
#include <set>
#include <algorithm>
#include <charconv>
#include <cstdint>
#include <cstdio>
namespace gmm {
struct Model { std::string spawn, display; };
inline std::string trim(std::string s) {
 auto first=s.find_first_not_of(" \t\r\n");
 return first==std::string::npos ? "" : s.substr(first,s.find_last_not_of(" \t\r\n")-first+1);
}
inline std::string lower(std::string s) { for(auto& c:s) if(c>='A'&&c<='Z') c+=32; return s; }
inline bool utf8(const std::string& s) {
 size_t i=0;
 while(i<s.size()) {
  unsigned c=(unsigned char)s[i++], n=0, value=0, minimum=0;
  if(c<128) continue;
  if(c>=0xc2&&c<=0xdf) { n=1; value=c&31; minimum=0x80; }
  else if(c>=0xe0&&c<=0xef) { n=2; value=c&15; minimum=0x800; }
  else if(c>=0xf0&&c<=0xf4) { n=3; value=c&7; minimum=0x10000; }
  else return false;
  if(i+n>s.size()) return false;
  while(n--) { auto b=(unsigned char)s[i++]; if((b&0xc0)!=0x80) return false; value=(value<<6)|(b&63); }
  if(value<minimum||value>0x10ffff||(value>=0xd800&&value<=0xdfff)) return false;
 } return true;
}
struct KeyName {unsigned vk;const char* name;};
inline constexpr KeyName namedKeys[]={
 {9,"Tab"},{0x13,"Pause"},{0x14,"CapsLock"},{0x1b,"Escape"},{0x20,"Space"},
 {0x21,"PageUp"},{0x22,"PageDown"},{0x23,"End"},{0x24,"Home"},{0x25,"Left"},{0x27,"Right"},
 {0x2c,"PrintScreen"},{0x2d,"Insert"},{0x2e,"Delete"},{0x5d,"Apps"},
 {0x6a,"Multiply"},{0x6b,"Add"},{0x6d,"Subtract"},{0x6e,"Decimal"},{0x6f,"Divide"},
 {0x90,"NumLock"},{0x91,"Scroll"},{0xba,"Oem1"},{0xbb,"Oemplus"},{0xbc,"Oemcomma"},
 {0xbd,"OemMinus"},{0xbe,"OemPeriod"},{0xbf,"OemQuestion"},{0xc0,"Oemtilde"},
 {0xdb,"OemOpenBrackets"},{0xdc,"OemPipe"},{0xdd,"OemCloseBrackets"},{0xde,"OemQuotes"},{0xdf,"Oem8"},{0xe2,"Oem102"}};
inline bool allowedMenuKey(unsigned code) {
 if((code>='A'&&code<='Z')||(code>='0'&&code<='9')||(code>=0x70&&code<=0x7b))return true;
 // NumPad 2/4/5/6/8 navigate the menu and NumPad0 closes it, so they cannot be the toggle.
 if(code>=0x61&&code<=0x69&&code!=0x62&&code!=0x64&&code!=0x65&&code!=0x66&&code!=0x68)return true;
 if((code>=0xba&&code<=0xc0)||(code>=0xdb&&code<=0xdf)||code==0xe2)return true;
 for(unsigned candidate:{9u,0x13u,0x14u,0x1bu,0x20u,0x21u,0x22u,0x23u,0x24u,0x25u,0x27u,0x2cu,0x2du,0x2eu,0x90u,0x91u})if(candidate==code)return true;
 return false;
}
inline std::string keyName(unsigned key) {
 if(!key)return "None";
 if((key>='A'&&key<='Z')||(key>='0'&&key<='9'))return std::string(1,char(key));
 if(key>=0x70&&key<=0x7b)return "F"+std::to_string(key-0x6f);
 if(key>=0x60&&key<=0x69)return "NumPad"+std::to_string(key-0x60);
 for(auto entry:namedKeys)if(entry.vk==key)return entry.name;
 return "L";
}
inline std::vector<Model> catalog(const std::string& text) {
 std::vector<Model> out; std::set<std::string> seen;
 if(text.size()>1024*1024) return out;
 std::istringstream stream(text.starts_with("\xEF\xBB\xBF")?text.substr(3):text); std::string line;
 while(out.size()<1024 && std::getline(stream,line)) {
  line=trim(line); if(line.empty()||line[0]=='#') continue;
  auto tab=line.find('\t'); if(tab==std::string::npos) continue;
  auto model=trim(line.substr(0,tab)), label=trim(line.substr(tab+1));
  if(model.empty()||model.size()>128||label.empty()||label.size()>160||!utf8(label)) continue;
  if(model=="."||model==".."||!std::all_of(model.begin(),model.end(),[](unsigned char c){return c>=32&&c<=126&&std::string("<>:\"/\\|?*~").find(c)==std::string::npos;})) continue;
  label.erase(std::remove_if(label.begin(),label.end(),[](unsigned char c){return c<32||c==127||c=='~';}),label.end());
  if(label.empty()||!seen.insert(lower(model)).second) continue;
  out.push_back({model,label});
 } return out;
}
inline unsigned menuKey(const std::string& text) {
 std::istringstream stream(text.starts_with("\xEF\xBB\xBF")?text.substr(3):text); std::string line,section;
 while(std::getline(stream,line)) {
  auto comment=line.find_first_of(";#");if(comment!=std::string::npos)line.resize(comment);
  line=trim(line); if(line.empty()) continue;
  if(line.front()=='['&&line.back()==']') {section=lower(trim(line.substr(1,line.size()-2)));continue;}
  auto equal=line.find('='); if(section!="menu"||equal==std::string::npos||lower(trim(line.substr(0,equal)))!="menukey") continue;
  auto key=lower(trim(line.substr(equal+1)));
  if(key=="none"||key=="disabled")return 0;
  if(key.find('+')!=std::string::npos)return 0; // Unsupported chord, never weaken it to a plain key.
  if(key.starts_with("vk_"))key=key.substr(3);
  if(key.starts_with("0x")||(key.size()>1&&key[0]>='0'&&key[0]<='9')) {
   bool hex=key.starts_with("0x");unsigned code=0;auto result=std::from_chars(key.data()+(hex?2:0),key.data()+key.size(),code,hex?16:10);
   if(result.ec==std::errc{}&&result.ptr==key.data()+key.size()&&allowedMenuKey(code))return code;
   return 0;
  }
  const KeyName aliases[]={{0x1b,"esc"},{0x21,"prior"},{0x22,"next"},{0xba,"oemsemicolon"},
   {0xbf,"oem2"},{0xc0,"oem3"},{0xdb,"oem4"},{0xdc,"oem5"},{0xdd,"oem6"},{0xde,"oem7"},{0x91,"scrolllock"}};
  for(auto entry:aliases)if(key==entry.name)return entry.vk;
  if(key.size()==1&&key[0]>='a'&&key[0]<='z') return key[0]-32;
  if(key.size()==1&&key[0]>='0'&&key[0]<='9') return key[0];
  if(key.size()==2&&key[0]=='d'&&key[1]>='0'&&key[1]<='9')return key[1];
  for(unsigned n=1;n<=12;++n) if(key=="f"+std::to_string(n)) return 0x6f+n;
  for(unsigned n=1;n<=9;++n)if(n!=2&&n!=4&&n!=5&&n!=6&&n!=8&&key=="numpad"+std::to_string(n))return 0x60+n;
  for(auto entry:namedKeys)if(key==lower(entry.name))return allowedMenuKey(entry.vk)?entry.vk:0;
  return 0;
 } return 0x4c;
}
// Saved appearance of one ped model: drawable/texture for each of the 12 component slots and the props
// (hat, glasses, ears, watch, bracelet; prop drawable -1 = none). Unset slots keep the author's defaults.
struct Look {
 std::array<std::array<int,2>,12> comp{}; std::array<bool,12> hasComp{};
 std::array<std::array<int,2>,8> prop{}; std::array<bool,8> hasProp{};
};
// One line per model: 0xHASH<TAB>spawn name<TAB>c2=3.1 c11=0.0 p0=-1.0 ...
inline std::string formatLook(std::uint32_t hash,const std::string& model,const Look& look) {
 char head[16]; std::snprintf(head,sizeof head,"0x%08X",hash);
 std::string line=std::string(head)+"\t"+model+"\t";
 for(int i=0;i<12;++i) if(look.hasComp[i]) line+="c"+std::to_string(i)+"="+std::to_string(look.comp[i][0])+"."+std::to_string(look.comp[i][1])+" ";
 for(int i=0;i<8;++i) if(look.hasProp[i]) line+="p"+std::to_string(i)+"="+std::to_string(look.prop[i][0])+"."+std::to_string(look.prop[i][1])+" ";
 return trim(line);
}
inline bool parseLookLine(const std::string& raw,std::uint32_t& hash,Look& look) {
 auto line=trim(raw); if(line.empty()||line[0]=='#') return false;
 auto tab=line.find('\t'); if(tab==std::string::npos||!line.starts_with("0x")) return false;
 unsigned long long value=0; auto r=std::from_chars(line.data()+2,line.data()+tab,value,16);
 if(r.ec!=std::errc{}||r.ptr!=line.data()+tab||value>0xFFFFFFFFull) return false;
 hash=static_cast<std::uint32_t>(value); look={};
 auto second=line.find('\t',tab+1); std::istringstream fields(second==std::string::npos?"":line.substr(second+1)); std::string field;
 while(fields>>field) {
  if(field.size()<5||(field[0]!='c'&&field[0]!='p')) continue;
  auto eq=field.find('='),dot=field.find('.',eq==std::string::npos?0:eq);
  if(eq==std::string::npos||dot==std::string::npos) continue;
  int slot=-1,drawable=0,texture=0;
  if(std::from_chars(field.data()+1,field.data()+eq,slot).ec!=std::errc{}) continue;
  if(std::from_chars(field.data()+eq+1,field.data()+dot,drawable).ec!=std::errc{}) continue;
  if(std::from_chars(field.data()+dot+1,field.data()+field.size(),texture).ec!=std::errc{}) continue;
  if(texture<0||texture>255||drawable<-1||drawable>1023) continue;
  if(field[0]=='c'&&slot>=0&&slot<12&&drawable>=0) {look.comp[slot]={drawable,texture};look.hasComp[slot]=true;}
  if(field[0]=='p'&&slot>=0&&slot<8) {look.prop[slot]={drawable,texture};look.hasProp[slot]=true;}
 }
 return true;
}
inline bool findLook(const std::string& text,std::uint32_t hash,Look& out) {
 if(text.size()>1024*1024) return false;
 std::istringstream stream(text); std::string line;
 while(std::getline(stream,line)) { std::uint32_t h=0; Look look; if(parseLookLine(line,h,look)&&h==hash) {out=look;return true;} }
 return false;
}
// Replace (or with look==nullptr remove) the model's line, keeping every other model's saved look.
inline std::string upsertLook(const std::string& text,std::uint32_t hash,const std::string& model,const Look* look) {
 std::istringstream stream(text.size()>1024*1024?std::string():text); std::string line,out="# GMM PED Selector: saved looks (written by the in-game menu)\n";
 int kept=0;
 while(std::getline(stream,line)&&kept<4096) { std::uint32_t h=0; Look l; if(!parseLookLine(line,h,l)||h==hash) continue; out+=trim(line)+"\n"; ++kept; }
 if(look) out+=formatLook(hash,model,*look)+"\n";
 return out;
}
using Keys=std::array<bool,256>;
struct Frame { bool open=false,blockAll=false,closeHeld=false,apply=false; int move=0,change=0; };
class Input {
 Keys previous{}; bool open=false, closeLatch=false;
public:
 Frame update(const Keys& keys,bool focused,unsigned toggle) {
  Frame f;
  auto edge=[&](unsigned k){return keys[k]&&!previous[k];};
  if(!focused) {open=false;closeLatch=false;previous=keys;return f;}
  bool was=open;
  if(toggle>0&&toggle<256&&edge(toggle)) open=!open;
  if(open && (edge(8)||edge(0x60))) {open=false;closeLatch=true;}
  if(closeLatch&&!keys[8]&&!keys[0x60]) closeLatch=false;
  f.open=open; f.blockAll=was||open; f.closeHeld=closeLatch;
  if(open) {
   if(edge(0x26)||edge(0x68)) f.move=-1;
   else if(edge(0x28)||edge(0x62)) f.move=1;
   f.apply=edge(0x0d)||edge(0x65);
   // Left/Right change a value in the customization page (never when that key is the menu toggle).
   if((toggle!=0x25&&edge(0x25))||edge(0x64)) f.change=-1;
   else if((toggle!=0x27&&edge(0x27))||edge(0x66)) f.change=1;
  }
  previous=keys;return f;
 }
};
}
