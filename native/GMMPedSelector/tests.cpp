#include "core.h"
#include <iostream>
#include <stdexcept>
void check(bool ok, const char* why) { if (!ok) throw std::runtime_error(why); }
int main() {
 try {
  auto rows = gmm::catalog("\xEF\xBB\xBF# comment\r\narthur\tArthur Morgan\r\nARTHUR\tduplicate\n../bad\tbad\nped_2\tSecond\n");
  check(rows.size()==2 && rows[0].spawn=="arthur" && rows[1].display=="Second", "catalog BOM, comment, deduplication and model filtering");
  auto external=gmm::catalog("therock(jeans)\tThe Rock jeans\ntherock(nojeans)\tThe Rock no jeans\n");
  check(external.size()==2&&external[0].spawn=="therock(jeans)"&&external[1].spawn=="therock(nojeans)","external technical names preserve parentheses exactly");
  check(gmm::catalog("bad\nfoo\t~r~Name\n").at(0).display=="rName", "display formatting must not inject HUD markup");
  check(gmm::catalog("ok\t\xC0\xAF\n").empty(), "invalid UTF8 rejected");
  check(gmm::menuKey("[Other]\nMenuKey=F8\n[Menu]\nMenuKey=f6\n")==0x75, "INI section and F key parsing");
  check(gmm::menuKey("[Menu]\nMenuKey=Enter\n")==0, "reserved key disables toggle");
  check(gmm::menuKey("\xEF\xBB\xBF[Menu]\r\nMenuKey=F9\r\n")==0x78, "UTF8 BOM INI must retain custom toggle");
  check(gmm::menuKey("[Menu]\nMenuKey=None\n")==0,"manager None must disable toggle");
  check(gmm::menuKey("[Menu]\nMenuKey=OemCloseBrackets\n")==0xdd,"manager FormsName key spelling");
  check(gmm::menuKey("[Menu]\nMenuKey=NumPad7\n")==0x67,"manager numpad spelling");
  check(gmm::menuKey("[Menu]\nMenuKey=Oem6\n")==0xdd,"Windows OEM alias");
  check(gmm::menuKey("[Menu]\nMenuKey=0x77\n")==0x77,"hex virtual key");
  check(gmm::menuKey("[Menu]\nMenuKey=VK_F7\n")==0x76,"VK key spelling");
  check(gmm::menuKey("[Menu]\nMenuKey=Ctrl+L\n")==0,"unsupported chord must never become unmodified toggle");
  check(gmm::menuKey("[Menu]\nMenuKey=Disabled\n")==0,"disabled alias");
  check(gmm::menuKey("[Menu]\nMenuKey=L ; Default L\n")==0x4c,"INI inline comment");
  check(gmm::menuKey("[Menu]\nMenuKey=76# inline\n")==0x4c,"decimal VK with comment");
  check(gmm::menuKey("[Menu]\nMenuKey=Oem102\n")==0xe2,"international OEM key");
  check(gmm::menuKey("[Menu]\nMenuKey=0x10\n")==0,"modifier-only toggle forbidden");
  check(gmm::keyName(0x75)=="F6"&&gmm::keyName(0x4c)=="L"&&gmm::keyName(0)=="None","HUD toggle label follows parsed key");
  {gmm::Input disabled;gmm::Keys all{};all.fill(true);check(!disabled.update(all,true,0).open,"disabled toggle cannot open");}
  gmm::Input input; gmm::Keys keys{};
  keys[0x4c]=true; auto frame=input.update(keys,true,0x4c);
  check(frame.open && frame.blockAll, "opening must block gameplay immediately");
  frame=input.update(keys,true,0x4c); check(frame.open, "held toggle must not repeat");
  keys={}; input.update(keys,true,0x4c); keys[0x68]=true;
  check(input.update(keys,true,0x4c).move==-1, "numpad8 must select up");
  keys={}; input.update(keys,true,0x4c); keys[0x28]=true;
  check(input.update(keys,true,0x4c).move==1, "arrow down must select down");
  keys={}; input.update(keys,true,0x4c); keys[0x65]=true;
  check(input.update(keys,true,0x4c).apply, "numpad5 must apply");
  keys={}; input.update(keys,true,0x4c); keys[0x60]=true;
  frame=input.update(keys,true,0x4c);
  check(!frame.open && frame.blockAll, "numpad0 must close and suppress close frame");
  frame=input.update(keys,true,0x4c); check(!frame.blockAll && frame.closeHeld, "only close actions suppressed after close");
  keys={}; frame=input.update(keys,true,0x4c); check(!frame.closeHeld && !frame.blockAll, "release restores all input");
  keys[0x4c]=true; input.update(keys,false,0x4c);
  check(!input.update(keys,true,0x4c).open, "focus regain must not treat held key as fresh press");
  keys={}; input.update(keys,true,0x4c); keys[0x4c]=true; input.update(keys,true,0x4c);
  keys={}; input.update(keys,true,0x4c); keys[8]=true;
  check(!input.update(keys,true,0x4c).open, "Backspace must close");
  for(auto navigation : {0x26,0x68,0x28,0x62,0x0d,0x65}) {
   gmm::Input fresh; gmm::Keys state{}; state[0x4c]=true;fresh.update(state,true,0x4c);
   state={};fresh.update(state,true,0x4c);state[navigation]=true;auto result=fresh.update(state,true,0x4c);
   check(result.blockAll,"every navigation/apply key keeps gameplay blocked");
   if(navigation==0x26||navigation==0x68) check(result.move==-1,"up aliases");
   else if(navigation==0x28||navigation==0x62) check(result.move==1,"down aliases");
   else check(result.apply,"apply aliases");
  }
  {gmm::Input lr;gmm::Keys st{};st[0x4c]=true;lr.update(st,true,0x4c);st={};lr.update(st,true,0x4c);st[0x27]=true;
   check(lr.update(st,true,0x4c).change==1,"right arrow changes a value");st={};lr.update(st,true,0x4c);st[0x25]=true;
   check(lr.update(st,true,0x4c).change==-1,"left arrow changes a value");st={};lr.update(st,true,0x4c);st[0x66]=true;
   check(lr.update(st,true,0x4c).change==1,"numpad6 changes a value");st={};lr.update(st,true,0x4c);st[0x64]=true;
   check(lr.update(st,true,0x4c).change==-1,"numpad4 changes a value");
   check(gmm::menuKey("[Menu]\nMenuKey=NumPad4\n")==0&&gmm::menuKey("[Menu]\nMenuKey=NumPad6\n")==0,"numpad4/6 are reserved for the menu");
   gmm::Input tl;gmm::Keys t2{};t2[0x25]=true;tl.update(t2,true,0x25);t2={};tl.update(t2,true,0x25);t2[0x25]=true;
   check(tl.update(t2,true,0x25).change==0,"toggle key never doubles as value change");}
  {gmm::Look look; look.comp[2]={3,1}; look.hasComp[2]=true; look.prop[0]={-1,0}; look.hasProp[0]=true; look.prop[1]={2,4}; look.hasProp[1]=true;
   auto line=gmm::formatLook(0x1A2B3C4D,"therock(jeans)",look);
   check(line=="0x1A2B3C4D\ttherock(jeans)\tc2=3.1 p0=-1.0 p1=2.4","look line format");
   std::string file=gmm::upsertLook("",0x1A2B3C4D,"therock(jeans)",&look);
   file=gmm::upsertLook(file,0x11111111,"other",&look);
   gmm::Look back; check(gmm::findLook(file,0x1A2B3C4D,back)&&back.hasComp[2]&&back.comp[2][1]==1&&back.prop[0][0]==-1&&back.prop[1][1]==4,"look round trip");
   check(!back.hasComp[3]&&!back.hasProp[6],"unset slots stay unset (author defaults)");
   file=gmm::upsertLook(file,0x1A2B3C4D,"therock(jeans)",nullptr);
   check(!gmm::findLook(file,0x1A2B3C4D,back)&&gmm::findLook(file,0x11111111,back),"reset removes only this model");
   gmm::Look junk; check(gmm::findLook("0xZZ\tbad\tc2=1.1\n0x00000005\tx\tc99=1.1 c2=-5.0 c3=1.999\n",5,junk)&&!junk.hasComp[2]&&!junk.hasComp[3],"garbage fields ignored");}
  std::cout<<"PASS catalog, INI, keymap, edges, focus, blocking policy and saved looks\n"; return 0;
 } catch(const std::exception& e) { std::cerr<<e.what()<<'\n'; return 1; }
}
