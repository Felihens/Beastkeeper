# Changelog

## 0.8.5
- Saddles protect: a saddle gives armor to the animal wearing it, like a player's armor (Leather 2, Troll 6, Wolf 16, Lox 21, Carapace 25, Drake Saddle 20). / Selas protegem: a sela dá armadura ao animal que a usa, como a armadura do jogador (Leather 2, Troll 6, Wolf 16, Lox 21, Carapace 25, Sela de Drake 20).
- A saddled animal is never caught by a sneak attack (the game's x4 hit: one Deathsquito sting took 324 of a drake's 350 health). / Um animal com sela nunca toma ataque furtivo (o golpe x4 do jogo: uma picada de mosquito tirava 324 dos 350 de vida de um drake).
- Tamed health reworked so an animal with the saddle of its own tier takes about 5 hits of its biome's strong enemy: Leather 120, Troll 270, Wolf 395, Lox 460, Carapace 400 (the game's own health is kept when higher: bear 500, lox 1000, asksvin 800). / Vida dos domados refeita para que um animal com a sela do próprio tier aguente umas 5 porradas do inimigo forte do bioma: Leather 120, Troll 270, Wolf 395, Lox 460, Carapace 400 (a vida do próprio jogo fica quando é maior: urso 500, lox 1000, asksvin 800).
- Tamed animals heal only within 60 m of a Beast Totem: on their own from nothing to full in 5 minutes (tied, ridden or grazing), and by hand feeding. Away from a totem they don't heal and won't eat from your hand (the food is kept). / Animais domados só se curam a até 60 m de um Totem de Feras: sozinhos de zero a cheio em 5 minutos (amarrados, montados ou pastando), e comendo da mão. Longe de um totem eles não se curam nem comem da sua mão (a comida não é gasta).

## 0.8.1
- Performance: checking whether you hold a lasso or a saddle is much lighter, and the drake walk no longer freezes the game now and then (its file check runs in the background). / Desempenho: checar se você segura um laço ou uma sela ficou bem mais leve, e o andar do drake não trava mais o jogo de vez em quando (a checagem do arquivo roda em segundo plano).
- README: the "Troll" tier means tools made of troll hide, for the bear (trolls can't be tamed), and a step-by-step guide to hatching a drake. / README: a tier "Troll" são ferramentas de couro de troll, para o urso (trolls não podem ser domados), e um passo a passo para chocar um drake.

## 0.8.0
- Drakes lay eggs: two grown tamed drakes near a straw rug lay an egg on it every 3 game days. The egg waits standing on the rug, one per pair until someone takes it, and none while 6 or more grown drakes are within 100 m. Looking at the rug shows how the nest is doing.
- Drakes botam ovos: dois drakes adultos domados perto de um tapete de palha botam um ovo nele a cada 3 dias do jogo. O ovo espera em pé no tapete, um por casal até alguém pegar, e nenhum enquanto houver 6 ou mais drakes adultos num raio de 100 m. Olhar para o tapete mostra como está o ninho.
- A laid egg takes after its parents (more stars, more glow), and Moder's altar refuses it ("This egg is not worthy.").
- Um ovo botado puxa aos pais (mais estrelas, mais brilho), e o altar da Moder o recusa ("This egg is not worthy.").
- Hatched drakes drop one of the item their egg was woken with and no trophy; only frost drakes drop freeze glands.
- Drakes chocados soltam um do item que acordou o ovo deles e nenhum troféu; só drakes de gelo soltam glândulas de gelo.
- Eggs: looking at one says "Make an offering"; what to offer is for you to find out, or for Hugin to hint. Time away counts while the fire could still burn.
- Ovos: olhar para um diz "Make an offering"; o que oferecer é para você descobrir, ou para o Hugin dar a dica. O tempo longe conta enquanto a fogueira ainda podia queimar.
- Hugin, the game's raven, gives a short tip the first time you meet the lasso, a totem, a sleeping egg and a grown drake (only while the raven's tips are on). Console: bk_raven <tip>.
- Hugin, o corvo do jogo, dá uma dica curta na primeira vez que você encontra o laço, um totem, um ovo adormecido e um drake adulto (só com as dicas do corvo ligadas). Console: bk_raven <dica>.
- Beast Totem window: tabs by kind, filters by stars and glow, your favorite marked in gold in the list, Rename on every name, a "?" page of tips, and E closes it. Up to 10 animals of each kind rest in the totems.
- Janela do Totem das Feras: abas por tipo, filtros por estrelas e brilho, a sua favorita destacada em dourado na lista, Rename em todo nome, uma página "?" de dicas, e E fecha a janela. Até 10 animais de cada tipo descansam nos totens.
- Bringing your favorite back at a totem costs trophies of its kind: 3, or 5 from wolves on (no trophy: 5 food). Choosing another favorite while yours rests in a totem moves the old one to the totem's other animals.
- Trazer a favorita de volta num totem custa troféus da espécie dela: 3, ou 5 a partir do lobo (sem troféu: 5 comidas). Escolher outra favorita com a sua guardada num totem passa a antiga para os outros animais do totem.
- Orders wheel (hold Q): Stay, Follow or Attack for your favorite mount. Stay is dropped when the animal is lassoed or far from its spot.
- Roda de ordens (segure Q): Stay, Follow ou Attack para a sua montaria favorita. Stay cai quando o animal é laçado ou fica longe do ponto dele.
- Portals: your mount and the animals on your lasso go through with you, and you ride on at the other side.
- Portais: a sua montaria e os animais no seu laço passam com você, e você continua montado do outro lado.
- Riding: no getting on over your weight limit; with the hammer out E and Q don't get you off or open the wheel; the middle mouse button does nothing.
- Montaria: não dá para montar acima do limite de peso; com o martelo na mão E e Q não te desmontam nem abrem a roda; o botão do meio do mouse não faz nada.
- Drake Saddle: a drake trophy instead of dragon tears. The game's own lox, asksvin and moose saddles can't be made or put on any more.
- Sela de Drake: um troféu de drake no lugar das lágrimas de dragão. As selas do próprio jogo para lox, asksvin e alce não podem mais ser feitas nem colocadas.
- Drake attacks: each element has its own cooldown; the stone spike's smaller stones stay closer to it.
- Ataques do drake: cada elemento tem a sua própria espera; as pedras menores do espinho de pedra ficam mais perto dele.
- Windows no longer lose their background after a while.
- As janelas não perdem mais o fundo depois de um tempo.

## 0.7.0
- Frost drake: a beam of ice for 1 s that goes exactly where you aim, instead of the ice ball. The aim turns slowly while it lasts, the screen shakes a little, and its damage grows the longer it stays on one enemy. The blizzard starts small and grows while the beam keeps hitting.
- Drake de gelo: um raio de gelo de 1 s que vai exatamente onde você mira, no lugar da bola de gelo. A mira gira devagar enquanto ele dura, a tela treme um pouco, e o dano cresce quanto mais tempo ele fica num inimigo. A nevasca começa pequena e cresce enquanto o raio continua acertando.
- Stone drake: a stone spike bursts out of the ground with smaller stones around it, instead of the rock, and sinks after 1.5 s. Charging makes it bigger; enemies near its centre are thrown far.
- Drake de pedra: um espinho de pedra sai do chão com pedras menores em volta, no lugar da rocha, e afunda depois de 1,5 s. Carregar deixa ele maior; inimigos perto do centro voam longe.
- Everybody with the mod sees the beam, the blizzard and the spike. Drakes fighting on their own use them too.
- Todos com o mod veem o raio, a nevasca e o espinho. Drakes lutando sozinhos também usam esses ataques.
- While you aim from a drake, the camera moves over your shoulder.
- Ao mirar de um drake, a câmera vai para cima do ombro.
- Lox, asksvin and moose wear the game's own saddle for them and the rider sits in the game's own riding pose; riding itself works as before.
- Lox, asksvin e alce usam a sela do próprio jogo para eles e o cavaleiro senta na pose de montaria do próprio jogo; montar funciona como antes.
- Beast Totem: up to 10 other tamed animals can be stored inside it besides your favorite, and taken out at any totem.
- Totem das Feras: até 10 outros animais domados podem ser guardados dentro dele além da favorita, e tirados em qualquer totem.
- Riding: Shift switches running on and off when the game's Toggle run option is on; items near the animal's feet are picked up.
- Montaria: Shift liga e desliga a corrida quando a opção Alternar corrida do jogo está ligada; itens perto das patas do animal são pegos.
- Drakes stop at the shore of deep water instead of flying out over the sea.
- Drakes param na beira de água funda em vez de voar por cima do mar.

## 0.6.0
- Beast Totem: its animals graze inside an 8 m circle (was 12 m), shown while you place the totem or look at it; animals that stray walk back into it.
- Totem das Feras: os animais dele pastam dentro de um círculo de 8 m (antes 12 m), que aparece ao posicionar o totem ou olhar para ele; animais que se afastam voltam para dentro.
- Defenders no longer back away from enemies, and stop chasing 30 m away and come back (new setting ChaseLimit).
- Defensores não recuam mais dos inimigos, e param de perseguir a 30 m e voltam (nova opção ChaseLimit).
- Your favorite mount follows you when you are more than 40 m from any totem (new setting FollowAwayFromTotem). A favorite drake follows on the wing: it takes off once you have walked 3 m and lands after you stand still for 3 seconds.
- A sua montaria favorita segue você quando você está a mais de 40 m de qualquer totem (nova opção FollowAwayFromTotem). Um drake favorito segue voando: decola depois que você anda 3 m e pousa quando você fica parado por 3 segundos.
- Animals on the lasso keep a 1.5 m gap from you.
- Animais no laço mantêm 1,5 m de distância de você.
- Drakes: each drake keeps its own attack cooldown; marks on the ground show where an attack will land (new setting AttackPreview); on the ground a drake only walks (the GroundSprint setting is gone).
- Drakes: cada drake tem o próprio tempo de recarga de ataque; marcas no chão mostram onde o ataque vai cair (nova opção AttackPreview); no chão o drake só anda (a opção GroundSprint foi removida).
- Frost drake: its attack raises a blizzard (5 m) that slows enemies to half speed. The blizzard has its own 6-second wait, shown by a second icon beside the attack cooldown, which resets when the enemy dies. Ice shards now start outside the enemy they hit and fly through it.
- Drake de gelo: o ataque levanta uma nevasca (5 m) que deixa os inimigos com metade da velocidade. A nevasca tem a própria espera de 6 segundos, mostrada num segundo ícone ao lado da recarga do ataque, que zera quando o inimigo morre. Os estilhaços de gelo agora saem de fora do inimigo atingido e passam através dele.
- Riding: the saddle and rider also move with the animal's idle (an asksvin standing, a drake hovering), loops change smoothly, and the drake saddle sits in place.
- Montaria: a sela e o cavaleiro também acompanham o animal parado (um asksvin em pé, um drake pairando), as animações trocam suavemente, e a sela do drake fica no lugar.

## 0.5.0
- Beast Totem: press E on a totem to store your favorite mount inside it and take it out at any totem, with its saddle, stars, name and health. An animal can be the favorite of only one player.
- Totem das Feras: aperte E num totem para guardar a sua montaria favorita dentro dele e tirar em qualquer totem, com sela, estrelas, nome e vida. Um animal só pode ser favorito de um jogador.
- Drakes: new flight bar (one section per 5 seconds) that empties while flying; each attack costs one section; when it runs out the drake lands and flies again once the bar is full.
- Drakes: nova barra de voo (uma seção a cada 5 segundos) que esvazia enquanto voa; cada ataque gasta uma seção; quando acaba o drake pousa e só voa de novo com a barra cheia.
- Drake attacks: the aim is locked where you click; lightning reaches 40 m and has an aim help; frost shards fly at nearby enemies; the stone rock can be charged, rolls on the ground and breaks on structures or after 15 m; fire is a stream of 7 fireballs; the drake moves at half speed while attacking, the wind-up is quicker (same attacks per second), and an icon at the bottom right shows the cooldown.
- Ataques do drake: a mira fica travada onde você clica; o raio alcança 40 m e tem auxílio de mira; os estilhaços de gelo voam nos inimigos por perto; a rocha de pedra pode ser carregada, rola no chão e quebra em estruturas ou depois de 15 m; o fogo é um jato de 7 bolas de fogo; o drake se move a meia velocidade enquanto ataca, a preparação é mais rápida (mesmos ataques por segundo), e um ícone no canto inferior direito mostra o cooldown.
- Drakes on the ground are faster (full speed at once, 70% sideways). Unridden drakes fight back with their own element. Party members (Shieldmates) are not hurt by your drake.
- Drakes no chão são mais rápidos (velocidade máxima na hora, 70% de lado). Drakes sem cavaleiro revidam com o próprio elemento. Membros da party (Shieldmates) não levam dano do seu drake.
- Eggs: 35% of the drakes hatch with 1 star and 10% with 2; stars give more damage, speed and flight time.
- Ovos: 35% dos drakes nascem com 1 estrela e 10% com 2; estrelas dão mais dano, velocidade e tempo de voo.
- Fixes: the drake's collision is no longer a big block around it; the totem keeps a drake's element and glow; glowing drakes glow like the hares; tamed drakes roar less often; each drake keeps its own attack cooldown.
- Correções: a colisão do drake não é mais um bloco grande em volta dele; o totem guarda o elemento e o brilho do drake; drakes brilhantes brilham como as lebres; drakes domados rugem menos; cada drake tem o próprio cooldown de ataque.

## 0.4.0
- Riders are now seen on the animal by other players: before, the rider showed at the animal's feet on everyone else's game. Everyone riding together needs this version.
- Quem monta agora aparece em cima do animal para os outros jogadores: antes o cavaleiro aparecia nos pés do animal no jogo dos outros. Todos que montam juntos precisam desta versão.
- Beast Totem: your favorite mount can now be brought back even when the game never told you it died (it died while a friend or the server owned it). The totem offers it whenever the animal can't be found, and it comes back as your favorite.
- Totem da Fera: a sua montaria favorita agora pode ser trazida de volta mesmo quando o jogo nunca avisou que ela morreu (morreu quando um amigo ou o servidor era o dono dela). O totem oferece quando o animal não é encontrado, e ela volta como favorita.
- Much lighter on the framerate with many animals around: tamed animals no longer scan every creature in the world, and the lasso and tie checks run less often.
- Bem mais leve para o FPS com muitos animais por perto: animais domados não vasculham mais todas as criaturas do mundo, e as checagens de laço e amarra rodam menos vezes.

## 0.3.0
- Drakes: after Moder, take a dragon egg from a drake nest, lay it down, give it an item that picks its element (frost, fire, poison, stone or lightning) and keep it by a fire. It hatches into a tamed baby drake that grows up and takes the new Drake Saddle. Fly it, land and walk, and attack with its element (each hits as hard; weaknesses and resistances decide). Every element has its own colours, and some drakes glow.
- Drakes: depois da Moder, pegue um ovo de dragão de um ninho de drake, coloque no chão, dê um item que escolhe o elemento (gelo, fogo, veneno, pedra ou raio) e deixe perto de uma fogueira. Ele choca num filhote de drake domado que cresce e aceita a nova Sela de Drake. Voe, pouse e ande, e ataque com o elemento dele (todos batem igual; fraquezas e resistências decidem). Cada elemento tem suas cores, e alguns drakes brilham.
- Tamed animals have more health and hit harder, by tier, so they hold their own in their biome.
- Animais domados têm mais vida e batem mais forte, por tier, para aguentar o próprio bioma.
- The saddle and the rider now move with each mount's walk and run.
- A sela e o cavaleiro agora se movem com o passo e a corrida de cada montaria.
- Far fewer options in the config: only the essentials are left (on/off, favorite mount key, defending, camera shake, animal lists, detailed log).
- Bem menos opções na config: só ficou o essencial (ligar/desligar, tecla da montaria favorita, defesa, tremor da câmera, listas de animais, log detalhado).

## 0.2.0
- Favorite mount: press U on a tamed animal that wears (or once wore) a saddle to make it your favorite. U anywhere else brings it to your side, wherever it is. If it dies, it comes back only at a Beast Totem: look at the totem and press U, paying 5 food from your bag or chests within 30 m. It returns tamed, with its stars and name, without a saddle. Option `10 - Favorite mount / CallKey`.
- Montaria favorita: aperte U num animal domado que está (ou já esteve) de sela para torná-lo seu favorito. U em qualquer outro lugar traz ele para o seu lado, onde quer que esteja. Se morrer, só volta num Totem dos Animais: olhe para o totem e aperte U, pagando 5 comidas da mochila ou de baús a até 30 m. Ele volta domado, com as estrelas e o nome, sem sela. Opção `10 - Favorite mount / CallKey`.
- Tamed animals that can fight (wolf, boar, bear, asksvin, lox) now attack enemies within 15 m, even while tied, at home or on your lasso. Options `8 - Defending`.
- Animais domados que sabem lutar (lobo, javali, urso, asksvin, lox) agora atacam inimigos a até 15 m, mesmo amarrados, em casa ou no laço. Opções `8 - Defending`.
- Saddle keys no longer clash: Alt + E takes the saddle off, Shift + E renames. Halter-tamed animals (deer, bear...) can be renamed too.
- Os atalhos da sela não brigam mais: Alt + E tira a sela, Shift + E renomeia. Animais domados com cabresto (cervo, urso...) também podem ser renomeados.
- Fixes: halter-tamed animals no longer vanish when no player is near; tied animals stay within their rope; animals on your rope or following you get through doors and narrow gaps; tamed animals walk to food on the ground by themselves, so they grow and breed without hand feeding.
- Correções: animais domados com cabresto não somem mais quando não há jogador perto; animais amarrados ficam dentro do alcance da corda; animais no laço ou seguindo você passam por portas e passagens estreitas; animais domados vão sozinhos até a comida no chão, então crescem e procriam sem comer da mão.

## 0.1.0
- First release: lasso with a tug of war, leg-hold traps, halters (the only way to tame), saddles and riding with jumps, attacks and saddle bags, tying animals to trees and buildings, the Beast Totem, healing with food, hare breeding with a fur colour for each baby (some glow), and riding the sea serpent. Five tiers of every tool, from Leather to Carapace.
- Primeira versão: laço com cabo de guerra, armadilhas, cabrestos (o único jeito de domar), selas e montaria com pulo, ataques e alforjes, amarrar animais em árvores e construções, o Totem das Feras, cura com comida, criação de lebres com uma cor de pelo para cada filhote (algumas brilham) e montar na serpente marinha. Cinco tiers de cada ferramenta, de Couro a Carapaça.
