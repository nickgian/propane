define Peer = {Sprint, Level3}

define transit(X,Y) = (enter(X) and exit(Y)) or (enter(Y) and exit(X))

define notransit = { 
	true => not transit(Peer,Peer) 
}

define preferences = { 
	true => exit(R1) >> exit(R2) >> exit(Peer) 
}

define ownership = {
	172.4.1.0/24 => any,
	true 		=> drop # TODO: remove this
}

define main = preferences and ownership and notransit
