# Historique des versions

Chaque version est publiée dans les [releases](../../releases) et proposée automatiquement dans
TeamPace par le bandeau de mise à jour.

## 4.4.0

- **Stratégie en direct** : en course, le plan est recalculé à chaque fin de tour à partir de la
  course telle qu'elle est (temps restant, essence, arrêts faits, relais en cours) et du rythme et
  de la consommation mesurés depuis le départ. Un arrêt fixé à la main que le réservoir ne permet
  plus d'atteindre avance. La page Course de l'overlay suit ce plan recalculé et rappelle le plan
  d'origine.
- **Pitwall** : une seconde fenêtre pour le coéquipier qui suit la course dans la session : relais
  en cours, essence et marge, prochain arrêt recalculé et ce qu'il faut régler dans le menu des
  stands (pilote, énergie, pneus), arrêts suivants, derniers tours. Rien à relier entre les PC :
  son SimHub lit la voiture qu'il regarde. Section *Pitwall* de l'onglet Overlay.

## 4.3.1

- **Overlay** : les boutons du volant s'associent directement dans l'onglet Overlay, avec le
  « Cliquer pour configurer » habituel de SimHub (plus besoin de passer par Contrôles et
  événements).

## 4.3.0

**Overlay en course (première version).**

- Un bandeau discret au-dessus du jeu, à placer où vous voulez, avec quatre pages qu'on fait
  défiler aux boutons du volant :
  - **Télémétrie** : vos pédales des dernières secondes, rapport, vitesse, écart en direct avec la
    référence ; le **fantôme** ajoute celles de la référence au même endroit de la piste ;
  - **Coaching** : les virages du Plan de travail, la consigne avant chacun avec un compte à
    rebours jusqu'au freinage de la référence, le bilan après, la voix en option ;
  - **Course** : le plan de stratégie en direct (tours avant l'arrêt, essence et marge, écart au
    plan, relais et pneus de l'arrêt) ;
  - **Plan de travail** : la progression de la séance virage par virage.
- Nouvel onglet **Overlay** : aperçu, taille, fond, pages, durée des courbes, référence (idéal de
  l'équipe, mon meilleur tour, meilleur tour de l'équipe ou d'un coéquipier), circuit, voix,
  virages travaillés, plan suivi.
- La référence « idéal de l'équipe » est un vrai tour : le meilleur passage de chaque virage, mis
  bout à bout avec ses pédales et sa vitesse.

## 4.2.0

**Stratégie : la qualif et le choix pneu par pneu.**

- **Qualif roulée (min)** dans l'en-tête de la préparation. La frise commence alors avant la
  course : **arrêt 0** (les pneus de la qualif), la barre de qualif avec son pilote et ses tours,
  **arrêt 1** (les pneus du départ de la course), puis les arrêts de course numérotés à partir
  de 2. L'usure de la qualif est reportée sur la course quand on part avec ces pneus, et ils
  comptent dans les pneus disponibles.
- **Choix des pneus sur une voiture vue de dessus** : chaque pneu montre ce qu'il lui reste en
  arrivant au stand ; un clic le change ou le garde (avant, arrière, un côté, les 4, aucun).
- L'usure est suivie **pneu par pneu**, d'après celle mesurée sur le circuit ; en automatique un
  essieu est changé quand un de ses pneus arriverait en fin de vie.
- **Télémétrie** : l'écart de chaque tour sur la référence dit « plus rapide » ou « plus lent ».

## 4.1.0

Audit technique : justesse des calculs et charge de la machine.

**Calculs corrigés**

- **Stratégie, temps aux stands** : il est maintenant calé sur vos **courses réelles**
  (« Arrêt complet en course » : médiane du temps perdu à vos arrêts, voie, service et relais
  compris). Un arrêt complet coûte ce temps, un arrêt plus léger moins. Avant, un bug mélangeait
  sur une voiture partagée le tour d'entrée d'un arrêt et le tour de sortie d'un autre.
- Ce que SimHub capte à un arrêt avec changement de pilote est coupé (le pilote qui descend ne voit
  plus la voiture) : ces arrêts ne servent plus à mesurer le débit d'essence ni le temps des pneus.
  L'essence et les pneus sont faits en même temps à l'arrêt.
- **Énergie virtuelle par tour** : moyenne robuste au lieu de la médiane. LMU l'écrit par paliers
  (3,1 / 3,5 %) et la médiane la surestimait d'environ 4 % (relais trop courts d'un tour).
- **Roulage** : sur une course partagée, chacun ne compte plus que sa part (avant : la durée de toute
  la course pour chaque pilote).
- **Arrêts d'une voiture partagée** (onglet Tours) : les mêmes que dans l'onglet Course, avec le temps
  perdu complet, chez le pilote qui est rentré au stand.
- Les notifications voient les sessions en équipage comme les écrans (pas de faux record).

**Moins de charge pendant que vous roulez**

- L'extraction de la télémétrie tourne en priorité basse et sur 2 cœurs au plus.
- L'enregistrement d'un tour invalide ne se fait plus dans la boucle de jeu ; plus d'allocation à
  chaque image.
- Un fichier de résultats n'est plus importé deux fois en parallèle.
- La surveillance de l'équipe (toutes les 5 minutes) ne relit les données que si un fichier a changé.
- Les sessions et les courses d'équipe ne sont relues que quand elles changent ; l'onglet *Course*
  se calcule en arrière-plan.

**Écran**

- **Consommation** : énergie virtuelle mise en avant, avec une jauge d'un plein découpée en tours,
  et l'essence à côté.

## 4.0.2

- **Usure des pneus** : la voiture vue de dessus, quatre pneus fins colorés selon l'usure par tour
  (du vert au corail) et la valeur à côté de chacun.
- **Tableau tour par tour** : il montre tous les tours, sans défilement interne ; c'est la page qui
  défile (molette comprise au-dessus du tableau).
- **Historique** : le type (courses, qualifs, essais) et le mode (hors ligne, en ligne, équipage)
  se filtrent par étiquettes cliquables, comme les catégories de la Carrière.

## 4.0.1

- **Toutes les sessions en équipage sont partagées** (essais, qualif, course), plus seulement les
  courses : chacun retrouve ses propres tours. Une qualif où un coéquipier a fait le chrono ne
  vous attribue plus son temps.
- Quand les pilotes d'un équipage n'ont pas vu la même chose (l'un a rejoint la session plus tard),
  la version du pilote dont les journaux LMU commencent le plus tôt l'emporte : un pilote arrivé en
  cours de course ne se voit plus attribuer les tours de son coéquipier.
- **Trois modes de session** : *hors ligne*, *en ligne* (seul dans la voiture) et *équipage* (en
  ligne, voiture partagée). Un repère sur chaque ligne de l'historique et dans l'analyse, et un
  filtre **Mode** dans l'historique.
- Deux pilotes sur la même séance d'essais en ligne sont reconnus ensemble (même fin de séance),
  même quand LMU ne dit pas clairement quand la séance a commencé.
- **Courbe d'écarts** : survoler le nom d'une équipe à droite met sa courbe en évidence, un clic la
  garde (● ; nouveau clic pour relâcher). Notre voiture reste à demi visible pour comparer.
- Toutes les sessions sont relues au premier démarrage.

## 4.0.0

Version de nettoyage : chaque écran a été repris pour être plus clair, plus visuel et sans texte
inutile, et les outils s'enchaînent d'une seule façon.

- **Navigation** : l'analyse d'une course a deux onglets, **Tours** et **Course**. *Course* réunit
  le prévu / réel face au plan de l'équipe et toutes les voitures sur une seule page : plus de
  passage par la stratégie ni de sous-écran « Toutes les voitures ». La stratégie montre le même
  bloc dans son onglet *Analyse de course*.
- **Analyse d'une session** :
  - en-tête sur une ligne (voiture · équipe), l'équipage et la capture directe au survol ;
    vitesse max retirée ;
  - statistiques épurées : carburant en litres par tour, énergie « 100 % = N tours », **usure des
    4 pneus en schéma coloré**, **arrêts sur une frise** (détail au survol) ;
  - **▾ Statistiques** et **▾ Tour par tour** se replient, chez tous les pilotes à la fois ;
  - sur une voiture partagée, le nombre d'arrêts est celui du pilote.
- **Page circuit** : record, roulage, sessions et tours par pilote, rien de plus ; plus de menu de
  circuits ni de bouton « Préparer une course » ; la courbe s'affiche avec **Courbe** à côté des
  onglets.
- **Toutes les voitures** : une case par voiture pour la courbe d'écarts (toutes par défaut,
  *Autour de nous*, *Aucune*), et une légende visible du déroulé de la course.
- **Plan de travail** : seules les mesures qui coûtent du temps sont affichées (le reste derrière
  « détail ») ; l'explication du calcul est passée dans l'aide.
- **Moins de texte partout** : sous-titres de la Carrière, de l'Historique et de la Stratégie,
  aides sous les titres, consignes de la télémétrie : tout est dans le bouton **?** et les
  info-bulles, réécrits pour chaque écran.
- **Historique** : le filtre « simulation complète » passe dans le menu **⋯**.

## 3.7.1

- **Stratégie, l'énergie d'abord** : chaque relais dure ce que permet l'énergie (ou le réservoir),
  le plein est remis à chaque arrêt et seulement le complément au dernier ; les pneus tiennent le
  plus longtemps possible. Les relais ne sont plus raccourcis pour être « équilibrés ».
- **Énergie de chaque arrêt** : on saisit le niveau voulu à la sortie des stands (100 %, 40 %…),
  comme dans le menu des stands de LMU, et non plus une quantité ajoutée. La saisie à la main
  fonctionne (elle ne faisait rien avant) ; « auto » revient au calcul.
- **Pneus pour la course** : un nombre de pneus (20 = 5 trains) au lieu d'un nombre de jeux.
- Retrait des « Variantes calculées » et de la liste des courses dans la préparation, des textes
  d'aide sous les titres, et du bouton « Page du circuit » de l'analyse d'une course.
- **Nouvelle icône** dans le menu de SimHub : un chronomètre aux aiguilles bleue et orange.

## 3.7.0

- **Stratégie refaite**, en trois onglets :
  - *Préparation* : le plan sur une frise des relais à l'échelle du temps. On **glisse un arrêt**
    pour le déplacer à un autre tour, on **glisse un pilote** sur un relais (ou clic sur le relais).
    Une carte par arrêt règle l'énergie, les pneus (auto, 0, 2, 4) et montre le relais, le temps
    arrêté et le temps perdu. Variantes calculées (4 pneus à chaque arrêt, sans pneus, le plein à
    chaque arrêt) et courses courues sur le circuit.
  - *Analyse de course* : une course réelle face au plan, prévu / réel sur la même échelle de
    temps, arrêt par arrêt avec le constat (conforme, trop long, imprévu…), le bilan, et la
    comparaison avec les autres équipes (l'écran « toutes les voitures » y est rattaché).
  - *Données mesurées* : les mesures de l'équipe, modifiables comme avant.
- **Courses en équipe partagées dans le dossier d'équipe** (`races`) : la course entière, chaque tour
  avec son pilote, le résultat et toutes les voitures. Chaque membre de l'équipage la voit avec ses
  propres tours, même si son coéquipier n'a pas encore mis à jour TeamPace (l'ancienne version
  prenait toute la voiture pour lui). Les deux résultats sont reconnus comme la même course.
- **Historique** : repère **ÉQUIPAGE** sur les courses en équipe et filtre « Courses en équipe ».
- Correction : le temps d'un tour sans chrono au moment d'un relais était faux (calculé jusqu'au
  relais suivant du même pilote) ; il est maintenant pris jusqu'au tour suivant de la voiture.
- Le bouton « Analyse de course » d'une course ouvre son analyse dans la stratégie.

## 3.6.0

- **Plan de travail** : la référence est maintenant le **tour idéal de l'équipe**, le meilleur
  passage de n'importe quel pilote dans chaque virage. Il est le même pour tous ; « Plan de travail
  de » choisit le pilote dont on voit les priorités. En haut : tour idéal, record de l'équipe (et qui
  le détient), record du pilote et son écart au tour idéal. Le record d'une qualif compte même
  après une longue course.
- **Télémétrie** : les valeurs sous le curseur (écart, vitesse, rapport, pédales, volant, régime,
  placement) s'affichent en gros au-dessus des courbes, une ligne par tour ; la carte et les virages
  restent visibles à droite.
- **Carrière** : le roulage s'affiche en heures entières (« 97 h »), et l'en-tête du tableau reste
  aligné avec les lignes quand la barre de défilement apparaît.
- **Progression** : la courbe est masquée par défaut (bouton « 📈 Graphique »).
- **Analyse** : chaque colonne défile, le tableau tour par tour reste accessible sous les blocs ;
  le temps au limiteur n'est plus affiché dans les arrêts (le temps dans la voie suffit).
- Une course rejointe en cours de route (relais) s'affiche à son heure de départ, la même pour tous
  les pilotes de la voiture. Les courses sont relues au premier démarrage.

## 3.5.0

- **Analyse de course** (bouton sur l'analyse d'une course) : toutes les voitures de la course,
  catégorie par catégorie, avec leurs arrêts, leurs relais, leur rythme et le temps perdu en piste.
  - *Où le temps est parti* : l'écart avec le vainqueur (ou la voiture choisie) découpé en rythme,
    arrêts et temps perdu en piste.
  - *Ce qui a marché, ce qui est à revoir* : comparaison avec les cinq premiers de la catégorie
    (nombre d'arrêts, arrêts trop longs, relais, rythme, sorties et contacts).
  - Déroulé de la course de chaque voiture (un arrêt = une barre aussi large que le temps perdu,
    orange quand le pilote change) et courbe d'écart au premier, tour par tour.
- Les arrêts des autres voitures sont repérés au temps perdu : LMU n'écrit pas leur carburant,
  leur énergie ni leurs pneus.
- Les courses déjà importées sont relues automatiquement au premier démarrage.

## 3.4.0

- **Correction importante, courses en équipe** : quand une voiture était partagée (relais entre
  pilotes), toute la course (tours et résultat) n'apparaissait que chez le pilote de l'arrivée. LMU
  n'écrit en effet qu'un résultat par voiture, au nom du dernier pilote. TeamPace retrouve
  maintenant qui a conduit chaque tour (relais et passages sur la ligne enregistrés par LMU) :
  chacun garde **ses tours**, avec le résultat de la voiture, et la carte de session montre
  l'équipage (« équipage : Amaury Marin 75 · Pascal Marin 51 tours »).
- LMU écrit un nouveau fichier de résultats à chaque fois qu'on rejoint une course en cours (après
  un relais, une déconnexion) : ces fichiers sont rassemblés et la course n'est gardée qu'une fois,
  dans sa version la plus complète.
- Correction : l'heure de départ d'une séance rejointe en cours de route était celle de l'arrivée
  dans la séance ; c'est maintenant le vrai départ, commun à tous les pilotes.
- Toutes les courses sont relues automatiquement au premier démarrage de cette version.

## 3.3.0

- **Stratégie** : le ravitaillement se règle en **énergie virtuelle**, comme dans LMU (auto, plein à
  100 % ou un pourcentage) ; l'essence suit, avec les litres par % mesurés pour chaque pilote, et
  le temps d'arrêt en découle. Les voitures sans énergie virtuelle restent en litres.
- Correction : le temps de ravitaillement était surestimé sur les voitures à énergie virtuelle
  (100 % d'énergie était compté comme un réservoir plein d'essence).

## 3.2.0

- **Stratégie** : carburant au choix à chaque arrêt (auto, plein ou litres tapés à la main). Le temps
  d'arrêt et la longueur du relais suivant suivent ; un plein permet de pousser un relais complet,
  carburant et pneus.
- Documentation et capture de la stratégie mises à jour.

## 3.1.0

- **Correction importante** : quand Google Drive change de lettre de lecteur (J: → I: après un
  redémarrage), TeamPace ne partageait plus rien sans prévenir (base, télémétrie). Il retrouve
  maintenant le dossier d'équipe sur sa nouvelle lettre, et affiche un ⚠ s'il reste introuvable.
- **Stratégie** : nombre de pneus changés au choix à chaque arrêt (auto, aucun, 1 à 4), avec le temps
  d'arrêt qui suit ; temps de changement de 2 pneus mesuré ou réglable.
- **Stratégie** : les plans enregistrés sont partagés avec l'équipe par le dossier d'équipe
  (« Plans de l'équipe », avec l'auteur et la date) ; les pilotes d'un plan sont repérés par leur nom,
  pour qu'un plan s'ouvre chez chacun.
- Les pop-ups de notification ne cachent plus les boutons de l'en-tête.

## 3.0.0

- **Nouvelle navigation** : trois onglets (Carrière, Historique, Stratégie) et une **page circuit**
  avec trois onglets (Progression, Télémétrie, Plan de travail) ; deux niveaux au plus. Un menu
  change de circuit sans revenir en arrière.
- **Carrière face à face** : une ligne par circuit et catégorie, le record de chaque pilote sur la
  même ligne dans un bloc à sa couleur, qui mène et de combien ; chiffres clés comparés ;
  « records détenus ».
- **Activité** : la légende disparaît ; un clic sur une barre sélectionne la ligne du circuit, un
  clic sur une ligne fait ressortir ses barres chez chaque pilote.
- **Historique** : une ligne par session, groupée par jour, le résultat de chaque pilote sur la même
  ligne ; filtres par catégorie et par circuit ; sessions vides masquées.
- **Plan de travail** (nouveau) : les trois virages où gagner du temps, face à ton meilleur passage,
  un coéquipier ou le meilleur de l'équipe ; la phase en cause (freinage, corde, sortie) et la mesure
  qui l'explique ; ton tour idéal.
- **Stratégie** (nouveau) : planificateur d'endurance. Rythme, dégradation, consommation, énergie
  virtuelle, usure et temps d'arrêt mesurés sur vos sessions (modifiables) ; relais équilibrés,
  arrêts détaillés, choix du pilote de chaque relais, plans enregistrés.
- **Notifications** (nouveau) : record battu ou amélioré, victoire, podium, session commune,
  télémétrie reçue ; pop-up à l'arrivée et cloche avec le nombre de nouveautés non lues.
- **Télémétrie** : force G, ABS et antipatinage ajoutés à l'extraction (relue automatiquement au
  premier « ⟳ Tout mettre à jour ») ; noms des virages au-dessus des courbes.
- Les lignes d'aide permanentes sont remplacées par des info-bulles et un bouton **?** qui déplie
  l'aide de chaque écran (avec la légende de l'analyse).
- Textes agrandis d'un cran, boutons harmonisés, temps de secteur au même format que le reste
  (« 28.569 »).
- Documentation et captures mises à jour.

## 2.7.1

- Le tracé des circuits (bords de piste, virages) utilise les tours de toute l'équipe, même des
  pilotes non affichés : un circuit roulé par un seul coéquipier a quand même son tracé.
- La courbe « Trajectoire » est retirée : la carte montre les trajectoires plus clairement.
- Les commandes de la carte (« ⤢ Agrandir », clic, molette) sont regroupées en haut de la carte.

## 2.7.0

- **Trajectoires** : la carte suit le zoom des courbes et montre la piste avec ses bords, la ligne
  de chaque tour, le début de freinage et la remise des gaz de chaque pilote. Précision au
  centimètre grâce au placement latéral enregistré par LMU.
- **Carte agrandie** : bouton « ⤢ Agrandir la carte » pour comparer les trajectoires en grand,
  les courbes principales restant affichées dessous.
- Nouvelle courbe **Trajectoire** : placement sur la largeur de la piste, mètre par mètre.
- **Virages** détectés automatiquement, avec le temps gagné ou perdu sur la référence dans chacun ;
  un clic zoome dessus (sur les courbes et la carte).
- Clic et molette sur la carte pour zoomer, survol de la carte pour lire les valeurs.
- La télémétrie déjà extraite est relue automatiquement au premier « ⟳ Tout mettre à jour » pour
  y ajouter le placement sur la piste.

## 2.6.0

- **Télémétrie** : superposez jusqu'à 4 tours (vous, vos coéquipiers, n'importe quelle séance sur le
  circuit) : écart cumulé, vitesse, accélérateur, frein, rapport, volant et régime, avec le tracé du
  circuit. Zoom à la molette, valeurs au survol. TeamPace reprend la télémétrie que LMU enregistre
  déjà, historique compris, et la partage avec l'équipe par le dossier Google Drive.
- Un seul bouton **⟳ Tout mettre à jour** : import des résultats, télémétrie et synchronisation de
  l'équipe.
- Arrêts aux stands : un retour au garage (Échap) suivi d'une sortie n'est plus pris pour un arrêt.
  Le vrai arrêt du même tour s'affiche avec son temps et son carburant.
- Temps perdu aux stands : un tour de sortie écourté (retour au garage) n'est plus compté.
- Nombre de pneus changés (2 ou 4) détecté pendant l'arrêt.
- Documentation : section Télémétrie et captures d'écran mises à jour (bouton « Tout mettre à jour »).

## 2.5.0

- Tours invalides : seuls les tours invalidés par une coupure ou une sortie de piste sont comptés.
  Les tours de stand et de sortie du garage ne le sont plus.
- Nouveaux repères tour par tour, tirés du journal de LMU : SORTIE, AVERT (avertissement limites),
  LIM (limites sans conséquence), CHOC (contact), PÉN (pénalité), avec le détail au survol.
- Temps théorique : les meilleurs secteurs s'affichent au survol (une ligne gagnée).
- Arrêts aux stands : bloc sur deux largeurs, sans barre de défilement ; temps perdu calculé aussi
  pour les tours de stand sans chrono officiel.
- Correction : le tour qui commence dans les stands (sortie) n'est plus pris pour un passage au stand.
- Les fichiers LMU sont relus automatiquement au premier démarrage pour extraire ces informations.

## 2.4.0

- **Correction** : les données capturées en direct (carburant, usure des pneus, arrêts aux stands)
  pouvaient ne pas s'afficher dans l'analyse alors qu'elles étaient bien enregistrées. LMU écrit une
  heure de début de session décalée de quelques minutes, différente pour chaque pilote ; le
  rattachement se fait désormais par numéro de tour. Les sessions déjà roulées sont corrigées
  automatiquement.
- Temps théorique détaillé : les trois meilleurs secteurs et le tour d'où vient chacun.
- Tours invalides : chrono reconstitué (entre parenthèses, hors statistiques) et compteur
  « Invalides » dans la carte de session.

## 2.3.0

- Progression : la courbe se lit maintenant de façon naturelle, plus bas = plus rapide.
  Une courbe qui descend, c'est un pilote qui progresse.
- Carrière : un clic sur un circuit le met en évidence chez tous les pilotes et grise le reste
  (re-clic pour annuler). Les graphiques d'activité suivent.
- Documentation mise à jour.

## 2.2.0

- Navigation repensée : barre unique avec onglets, pilotes et bouton Réglages repliable.
- Titre de page et fil d'Ariane cliquable ; retour avec le bouton « précédent » de la souris ou Alt + ←.
- Progression d'un circuit : courbe commune à tous les pilotes et chiffres clés (record, écart
  équipe, progression, dernière séance, temps roulé).

## 2.1.0

- Analyse de session plus lisible : carte de session avec chiffres clés, blocs d'analyse alignés
  entre pilotes, sélection des tours au-dessus du tableau et légende en dessous.
- Nouveau : régularité (écart-type des tours sélectionnés).

## 2.0.0

- Best Lap Records devient **TeamPace**. Les données existantes sont reprises automatiquement.
- Guide d'installation illustré, avec le partage Google Drive pas à pas.

## 1.0.0

- Première version publique (sous le nom Best Lap Records) : carrière, historique, analyse tour par
  tour, capture en direct, comparaison jusqu'à 4 pilotes, partage d'équipe par Google Drive et
  mises à jour automatiques.
